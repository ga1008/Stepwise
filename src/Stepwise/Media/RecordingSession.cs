using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Stepwise.Media;

public sealed record RecordingOptions(bool SystemAudio = true, bool Microphone = true, int FramesPerSecond = 20);

/// <summary>Output frame rate and maximum pixel width; zero width keeps the source size.</summary>
public sealed record ExportOptions(int FramesPerSecond, int MaxWidth = 0);

/// <summary>Owns one capture and its recoverable source files. Disposing never deletes recordings.</summary>
public sealed class RecordingSession : IDisposable
{
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _warningsLock = new();
    private readonly List<string> _warnings = [];
    private readonly List<AudioCaptureTrack> _audio = [];
    private readonly Stopwatch _clock = new();
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _video;
    private Task? _stderrTask;
    private Task? _stdoutTask;
    private volatile bool _recording;
    private volatile bool _disposed;
    private DateTimeOffset _clockOrigin;
    private double _videoOrigin;
    private TimeSpan _duration;
    private SessionInfo _info = new();
    private readonly Queue<string> _errorTail = new();

    public string WorkingDirectory { get; }
    public List<string> Warnings { get { lock (_warningsLock) return [.. _warnings]; } }
    public bool IsRecording => _recording && _video is { HasExited: false };
    public TimeSpan Duration => _recording
        ? TimeSpan.FromSeconds(Math.Max(0, _clock.Elapsed.TotalSeconds - _videoOrigin)) : _duration;
    public static string FfmpegPath => Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
    private static string FfprobePath => Path.Combine(AppContext.BaseDirectory, "tools", "ffprobe.exe");
    private string VideoPath => Path.Combine(WorkingDirectory, "capture.mkv");

    private RecordingSession(string workingDirectory)
    {
        WorkingDirectory = Path.GetFullPath(workingDirectory);
    }

    public static async Task<RecordingSession> StartAsync(Rectangle bounds, string workingDirectory, RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (bounds.Width < 2 || bounds.Height < 2) throw new ArgumentException("录制区域太小。");
        if (options.FramesPerSecond is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(options));
        EnsureFfmpeg();
        var session = new RecordingSession(workingDirectory);
        Directory.CreateDirectory(session.WorkingDirectory);
        if (File.Exists(session.VideoPath) || File.Exists(Path.Combine(session.WorkingDirectory, "session.json")))
            throw new IOException("此录制目录已有内容，请使用新的步骤目录。");
        session._info = new SessionInfo
        {
            CreatedUtc = DateTimeOffset.UtcNow, Options = options,
            Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height
        };
        session._clockOrigin = DateTimeOffset.UtcNow;
        session._clock.Start();
        try
        {
            // Construct WASAPI objects on a worker to avoid capturing a WinForms SynchronizationContext.
            await Task.Run(() =>
            {
                if (options.SystemAudio) session.TryStartAudio("system.wav", "系统声音", true);
                if (options.Microphone) session.TryStartAudio("microphone.wav", "麦克风", false);
            }).ConfigureAwait(false);
            session._videoOrigin = session._clock.Elapsed.TotalSeconds;
            var args = new[]
            {
                "-hide_banner", "-y", "-loglevel", "info", "-stats_period", "0.2", "-progress", "pipe:1",
                "-thread_queue_size", "128", "-f", "gdigrab", "-framerate", options.FramesPerSecond.ToString(CultureInfo.InvariantCulture),
                "-draw_mouse", "1", "-offset_x", bounds.Left.ToString(CultureInfo.InvariantCulture),
                "-offset_y", bounds.Top.ToString(CultureInfo.InvariantCulture), "-video_size", $"{bounds.Width}x{bounds.Height}",
                "-probesize", "32", "-analyzeduration", "0", "-i", "desktop", "-an",
                "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20",
                "-pix_fmt", "yuv420p", "-g", (options.FramesPerSecond * 2).ToString(CultureInfo.InvariantCulture),
                "-fps_mode", "cfr", "-flush_packets", "1", "-f", "matroska", session.VideoPath
            };
            session._video = NewProcess(FfmpegPath, args, redirectInput: true);
            if (!session._video.Start()) throw new IOException("无法启动内置录制器。");
            session._recording = true;
            session._stderrTask = session.ReadCaptureErrorsAsync();
            session._stdoutTask = session.ReadCaptureProgressAsync();
            session.SaveInfo("recording");
            _ = session.ObserveUnexpectedExitAsync();
            var exit = session._video.WaitForExitAsync();
            var ready = await Task.WhenAny(session._firstFrame.Task, exit, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
            if (ready != session._firstFrame.Task || !session.IsRecording)
            {
                await session.StopAsync().ConfigureAwait(false);
                throw new IOException("桌面录制未能启动。" + session.LastError());
            }
            session.SaveInfo("recording");
            return session;
        }
        catch
        {
            await session.StopAsync().ConfigureAwait(false);
            session.Dispose();
            throw;
        }
    }

    /// <summary>Reopens source files after restart or a failed export. Capture is never resumed.</summary>
    public static RecordingSession Open(string workingDirectory)
    {
        var session = new RecordingSession(workingDirectory);
        var metadata = Path.Combine(session.WorkingDirectory, "session.json");
        if (File.Exists(metadata))
            session._info = JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(metadata))
                ?? throw new InvalidDataException("录制信息无法读取。");
        if (!File.Exists(session.VideoPath)) throw new FileNotFoundException("找不到原始录屏文件。", session.VideoPath);
        session._duration = TimeSpan.FromSeconds(Math.Max(0, session._info.DurationSeconds));
        session._videoOrigin = Math.Max(0, session._info.VideoOriginSeconds);
        foreach (var warning in session._info.Warnings) session.AddWarning(warning);
        if (session._info.State == "recording") session.AddWarning("上次录制意外结束，将尽力恢复已经写入的内容。");
        return session;
    }

    private void TryStartAudio(string filename, string label, bool loopback)
    {
        try
        {
            var track = AudioCaptureTrack.Start(Path.Combine(WorkingDirectory, filename), label, loopback, _clock, AddWarning);
            _audio.Add(track);
        }
        catch (Exception e)
        {
            AddWarning($"{label}不可用，继续录制画面：{e.Message}");
        }
    }

    public async Task StopAsync()
    {
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_recording && _audio.Count == 0) return;
            _recording = false;
            var stopAt = _clock.Elapsed.TotalSeconds;
            _duration = TimeSpan.FromSeconds(Math.Max(0, stopAt - _videoOrigin));
            var audioStop = Task.WhenAll(_audio.Select(track => track.StopAsync(stopAt)));
            if (_video is not null)
            {
                try
                {
                    if (!_video.HasExited)
                    {
                        await _video.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
                        await _video.StandardInput.FlushAsync().ConfigureAwait(false);
                        await _video.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
                    }
                    else AddWarning("录制进程已提前结束，已保留可恢复内容。");
                    if (_video.ExitCode != 0) AddWarning("录制器异常结束，请查看 capture.log。已保留原始文件。");
                }
                catch (Exception e)
                {
                    AddWarning("录制器未正常结束，已保留可恢复内容：" + e.Message);
                    TryKill(_video);
                    try { await _video.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
                }
            }
            await audioStop.ConfigureAwait(false);
            _audio.Clear();
            if (_stdoutTask is not null) await _stdoutTask.ConfigureAwait(false);
            if (_stderrTask is not null) await _stderrTask.ConfigureAwait(false);
            _clock.Stop();
            var actual = await ProbeDurationAsync().ConfigureAwait(false);
            if (actual > 0) _duration = TimeSpan.FromSeconds(actual);
            SaveInfo("stopped");
        }
        finally { _operation.Release(); }
    }

    public async Task<string> ExportAsync(string destinationPath, bool gif, bool muted, IProgress<double>? progress = null,
        ExportOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (options is not null)
        {
            if (options.FramesPerSecond < 1 || options.FramesPerSecond > (gif ? 30 : 60))
                throw new ArgumentOutOfRangeException(nameof(options), gif ? "GIF 帧率须为 1–30。" : "MP4 帧率须为 1–60。");
            if (options.MaxWidth != 0 && options.MaxWidth is < 160 or > 7680)
                throw new ArgumentOutOfRangeException(nameof(options), "最大宽度须为 160–7680，或 0 表示原始大小。");
        }
        if (_recording) await StopAsync().ConfigureAwait(false);
        await _operation.WaitAsync().ConfigureAwait(false);
        string? temporary = null;
        try
        {
            EnsureFfmpeg();
            if (!File.Exists(VideoPath) || new FileInfo(VideoPath).Length < 100)
                throw new IOException("没有可导出的画面。过程文件已保留。");
            var destination = Path.GetFullPath(destinationPath);
            if (string.Equals(destination, VideoPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("导出文件不能覆盖原始录屏。");
            var expectedExtension = gif ? ".gif" : ".mp4";
            if (!string.Equals(Path.GetExtension(destination), expectedExtension, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"请选择 {expectedExtension} 文件名。", nameof(destinationPath));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileNameWithoutExtension(destination)}.{Guid.NewGuid():N}.partial{expectedExtension}");
            var args = new List<string> { "-hide_banner", "-y", "-loglevel", "warning", "-nostdin", "-progress", "pipe:1", "-i", VideoPath };
            if (_duration <= TimeSpan.Zero)
                _duration = TimeSpan.FromSeconds(await ProbeDurationAsync().ConfigureAwait(false));
            var copyVideo = false;
            if (gif)
            {
                options ??= new ExportOptions(12, 1440);
                var width = options.MaxWidth == 0 ? "iw" : $"min({options.MaxWidth},iw)";
                var filter = $"[0:v]fps={options.FramesPerSecond},scale=w='{width}':h='max(1,round(ih*ow/iw))':flags=lanczos,setsar=1,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=3";
                args.AddRange(["-filter_complex", filter, "-an", "-loop", "0", "-f", "gif"]);
            }
            else
            {
                var tracks = muted ? [] : ValidAudioPaths();
                foreach (var track in tracks)
                    args.AddRange(["-ss", _videoOrigin.ToString("0.000000", CultureInfo.InvariantCulture), "-i", track]);
                // Legacy callers retain the original stream. Explicit settings only re-encode when required.
                copyVideo = options is null || await CanCopyVideoAsync(options).ConfigureAwait(false);
                args.AddRange(["-map", "0:v:0"]);
                if (copyVideo) args.AddRange(["-c:v", "copy"]);
                else
                {
                    var width = options!.MaxWidth == 0 ? "iw" : $"min({options.MaxWidth},iw)";
                    var filter = $"fps={options.FramesPerSecond},scale=w='max(2,trunc(({width})/2)*2)':h='max(2,round(ih*ow/iw/2)*2)':flags=lanczos,setsar=1";
                    args.AddRange(["-vf", filter, "-c:v", "libx264", "-preset", "fast", "-crf", "20", "-pix_fmt", "yuv420p", "-fps_mode", "cfr"]);
                }
                if (tracks.Count > 0)
                {
                    var filter = new StringBuilder();
                    for (var i = 1; i <= tracks.Count; i++)
                        filter.Append($"[{i}:a]aresample=48000:async=1:first_pts=0,apad[a{i}];");
                    for (var i = 1; i <= tracks.Count; i++) filter.Append($"[a{i}]");
                    filter.Append($"amix=inputs={tracks.Count}:duration=longest:normalize=0,alimiter=limit=0.95:latency=1[aout]");
                    args.AddRange(["-filter_complex", filter.ToString(), "-map", "[aout]", "-c:a", "aac", "-b:a", "160k", "-ac", "2", "-shortest"]);
                }
                else args.Add("-an");
                args.AddRange(["-movflags", "+faststart", "-f", "mp4"]);
            }
            args.Add(temporary);
            progress?.Report(0);
            var logPath = Path.Combine(WorkingDirectory, $"export-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.log");
            var description = JsonSerializer.Serialize(new { Format = gif ? "gif" : "mp4", Muted = gif || muted,
                FramesPerSecond = options?.FramesPerSecond, MaxWidth = options?.MaxWidth, CopyVideo = copyVideo });
            await RunConversionAsync(args, logPath, progress, description).ConfigureAwait(false);
            if (!File.Exists(temporary) || new FileInfo(temporary).Length < 50)
                throw new IOException("导出文件为空，过程文件已保留。");
            try { File.Move(temporary, destination, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new IOException("无法写入导出文件，请关闭占用文件的程序或检查目录权限。原始录制与已有输出均已保留，可重试。", e);
            }
            SaveInfo("stopped");
            progress?.Report(1);
            return destination;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception e) { AddWarning("未完成的导出临时文件清理失败：" + e.Message); }
            }
            _operation.Release();
        }
    }

    private async Task<bool> CanCopyVideoAsync(ExportOptions options)
    {
        if (!File.Exists(FfprobePath)) return false;
        try
        {
            using var probe = NewProcess(FfprobePath, ["-v", "error", "-select_streams", "v:0", "-show_entries",
                "stream=codec_name,pix_fmt,width,height,avg_frame_rate,r_frame_rate", "-of", "json", VideoPath]);
            if (!probe.Start()) return false;
            var output = probe.StandardOutput.ReadToEndAsync();
            var errors = probe.StandardError.ReadToEndAsync();
            try { await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
            catch { TryKill(probe); return false; }
            await errors.ConfigureAwait(false);
            if (probe.ExitCode != 0) return false;
            using var document = JsonDocument.Parse(await output.ConfigureAwait(false));
            var stream = document.RootElement.GetProperty("streams")[0];
            var width = stream.GetProperty("width").GetInt32();
            var height = stream.GetProperty("height").GetInt32();
            bool HasExpectedFrameRate(string name)
            {
                var rational = stream.GetProperty(name).GetString()?.Split('/');
                return rational is { Length: 2 } &&
                    double.TryParse(rational[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
                    double.TryParse(rational[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
                    denominator > 0 && Math.Abs(numerator / denominator - options.FramesPerSecond) < .0001;
            }
            return stream.GetProperty("codec_name").GetString() == "h264" &&
                stream.GetProperty("pix_fmt").GetString() == "yuv420p" && width % 2 == 0 && height % 2 == 0 &&
                (options.MaxWidth == 0 || width <= options.MaxWidth) &&
                HasExpectedFrameRate("avg_frame_rate") && HasExpectedFrameRate("r_frame_rate");
        }
        catch { return false; }
    }

    /// <summary>Reads actual encoded dimensions, including H.264 even-pixel rounding and capture padding.</summary>
    public static async Task<Size> GetExportSizeAsync(string exportedPath)
    {
        if (!File.Exists(FfprobePath)) throw new FileNotFoundException("缺少内置媒体检查器 tools\\ffprobe.exe。", FfprobePath);
        using var probe = NewProcess(FfprobePath, ["-v", "error", "-select_streams", "v:0", "-show_entries",
            "stream=width,height", "-of", "json", Path.GetFullPath(exportedPath)]);
        if (!probe.Start()) throw new IOException("导出尺寸无法读取。");
        var output = probe.StandardOutput.ReadToEndAsync();
        var errors = probe.StandardError.ReadToEndAsync();
        try { await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        catch { TryKill(probe); throw new IOException("读取导出尺寸超时。"); }
        var error = await errors.ConfigureAwait(false);
        if (probe.ExitCode != 0) throw new IOException("导出尺寸无法读取：" + error.Trim());
        try
        {
            using var document = JsonDocument.Parse(await output.ConfigureAwait(false));
            var stream = document.RootElement.GetProperty("streams")[0];
            var size = new Size(stream.GetProperty("width").GetInt32(), stream.GetProperty("height").GetInt32());
            if (size.Width < 1 || size.Height < 1) throw new InvalidDataException("媒体尺寸为空。");
            return size;
        }
        catch (Exception e) { throw new IOException("导出尺寸无法读取。", e); }
    }

    private List<string> ValidAudioPaths()
    {
        var result = new List<string>();
        foreach (var name in new[] { "system.wav", "microphone.wav" })
        {
            var path = Path.Combine(WorkingDirectory, name);
            if (File.Exists(path) && new FileInfo(path).Length > 100) result.Add(path);
        }
        return result;
    }

    private async Task RunConversionAsync(List<string> args, string logPath, IProgress<double>? progress, string description)
    {
        await using var log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        await log.WriteLineAsync(description).ConfigureAwait(false);
        using var process = NewProcess(FfmpegPath, args);
        if (!process.Start()) throw new IOException("无法启动内置转换器。");
        async Task ReadErrors()
        {
            var logAvailable = true;
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                if (logAvailable)
                {
                    try { await log.WriteLineAsync(line).ConfigureAwait(false); }
                    catch (Exception e) { logAvailable = false; AddWarning("导出日志写入失败：" + e.Message); }
                }
        }
        async Task ReadProgress()
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    double.TryParse(line[12..], NumberStyles.Float, CultureInfo.InvariantCulture, out var us) && _duration.TotalSeconds > 0)
                    progress?.Report(Math.Clamp(us / 1_000_000d / _duration.TotalSeconds, 0, .99));
        }
        var errors = ReadErrors();
        var reporting = ReadProgress();
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(errors, reporting).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException($"导出失败（{process.ExitCode}），原始文件已保留，可重试。\n日志：{logPath}");
    }

    private async Task ReadCaptureErrorsAsync()
    {
        StreamWriter? log = null;
        try
        {
            try { log = new StreamWriter(Path.Combine(WorkingDirectory, "capture.log"), false, new UTF8Encoding(false)) { AutoFlush = true }; }
            catch (Exception e) { AddWarning("录制日志无法创建：" + e.Message); }
            while (await _video!.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (log is not null)
                {
                    try { await log.WriteLineAsync(line).ConfigureAwait(false); }
                    catch (Exception e) { AddWarning("录制日志写入失败：" + e.Message); try { log.Dispose(); } catch { } log = null; }
                }
                lock (_errorTail)
                {
                    _errorTail.Enqueue(line);
                    if (_errorTail.Count > 6) _errorTail.Dequeue();
                }
                // gdigrab timestamps the first desktop frame with wall-clock time; audio shares this origin.
                if (line.Contains("Duration: N/A, start:", StringComparison.Ordinal))
                {
                    var match = Regex.Match(line, @"start:\s*([0-9]+(?:\.[0-9]+)?)");
                    if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var unix))
                    {
                        var offset = unix - _clockOrigin.ToUnixTimeMilliseconds() / 1000d;
                        if (offset >= 0 && offset < _clock.Elapsed.TotalSeconds + 2) _videoOrigin = offset;
                    }
                }
            }
        }
        catch (Exception e) { AddWarning("录制日志写入失败：" + e.Message); }
        finally { if (log is not null) try { await log.DisposeAsync().ConfigureAwait(false); } catch { } }
    }

    private async Task ReadCaptureProgressAsync()
    {
        try
        {
            while (await _video!.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                if (line.StartsWith("frame=", StringComparison.Ordinal) && int.TryParse(line[6..], out var frame) && frame > 0)
                    _firstFrame.TrySetResult();
        }
        catch (Exception e) { AddWarning("无法读取录制状态：" + e.Message); }
    }

    private async Task ObserveUnexpectedExitAsync()
    {
        try
        {
            await _video!.WaitForExitAsync().ConfigureAwait(false);
            if (_recording)
            {
                AddWarning("画面录制提前结束，声音采集已自动停止。");
                await StopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e) { AddWarning("录制收尾失败，原始文件已保留：" + e.Message); }
    }

    private async Task<double> ProbeDurationAsync()
    {
        if (!File.Exists(FfprobePath) || !File.Exists(VideoPath)) return 0;
        try
        {
            using var probe = NewProcess(FfprobePath, ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", VideoPath]);
            if (!probe.Start()) return 0;
            var output = probe.StandardOutput.ReadToEndAsync();
            var errors = probe.StandardError.ReadToEndAsync();
            try { await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
            catch { TryKill(probe); return 0; }
            await errors.ConfigureAwait(false);
            return double.TryParse((await output.ConfigureAwait(false)).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
        }
        catch { return 0; }
    }

    private void SaveInfo(string state)
    {
        _info.State = state;
        _info.DurationSeconds = Duration.TotalSeconds;
        _info.VideoOriginSeconds = _videoOrigin;
        _info.Warnings = Warnings;
        var path = Path.Combine(WorkingDirectory, "session.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_info, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    private void AddWarning(string text)
    {
        lock (_warningsLock) if (!_warnings.Contains(text)) _warnings.Add(text);
    }

    private string LastError()
    {
        lock (_errorTail) return "\n" + string.Join("\n", _errorTail);
    }

    private static void EnsureFfmpeg()
    {
        if (!File.Exists(FfmpegPath)) throw new FileNotFoundException("缺少内置转换器 tools\\ffmpeg.exe，请完整解压程序后运行。", FfmpegPath);
    }

    private static Process NewProcess(string executable, IEnumerable<string> args, bool redirectInput = false)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = redirectInput,
            StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return new Process { StartInfo = info };
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { StopAsync().GetAwaiter().GetResult(); }
        finally
        {
            _disposed = true;
            _video?.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private sealed class SessionInfo
    {
        public SessionInfo() { }
        public int Version { get; set; } = 1;
        public DateTimeOffset CreatedUtc { get; set; }
        public string State { get; set; } = "stopped";
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double VideoOriginSeconds { get; set; }
        public double DurationSeconds { get; set; }
        public RecordingOptions Options { get; set; } = new();
        public List<string> Warnings { get; set; } = [];
    }
}
