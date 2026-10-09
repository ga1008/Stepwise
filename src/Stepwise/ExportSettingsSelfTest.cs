using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stepwise.Media;

namespace Stepwise;

/// <summary>Real FFmpeg exports from deterministic synthetic media; no desktop or microphone capture.</summary>
internal static class ExportSettingsSelfTest
{
    public static int Run(string outputRoot)
    {
        outputRoot = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(outputRoot);
        var run = Path.Combine(outputRoot, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        var checks = new List<string>();
        var details = new Dictionary<string, object>();
        try
        {
            RunAsync(run, checks, details).GetAwaiter().GetResult();
            WriteResult(new { passed = true, checks, details, run });
            return 0;
        }
        catch (Exception e)
        {
            WriteResult(new { passed = false, checks, details, run, error = e.ToString() });
            return 1;
        }
        void WriteResult(object result) => File.WriteAllText(Path.Combine(outputRoot, "export-settings-test-result.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static async Task RunAsync(string run, List<string> checks, Dictionary<string, object> details)
    {
        var source = Path.Combine(run, "source");
        Directory.CreateDirectory(source);
        var capture = Path.Combine(source, "capture.mkv");
        await RunToolAsync(RecordingSession.FfmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=2",
            "-map", "0:v", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", capture,
            "-map", "1:a", "-c:a", "pcm_s16le", Path.Combine(source, "system.wav"),
            "-map", "2:a", "-c:a", "pcm_s16le", Path.Combine(source, "microphone.wav")
        ]);
        var originalHash = Hash(capture);
        var originalSystem = Hash(Path.Combine(source, "system.wav"));
        var originalMicrophone = Hash(Path.Combine(source, "microphone.wav"));
        using var session = RecordingSession.Open(source);

        var scaled = await Export("mp4-15-320.mp4", false, false, new ExportOptions(15, 320));
        Assert(scaled.Width == 320 && scaled.Height == 180 && Math.Abs(scaled.FramesPerSecond - 15) < .01 &&
            scaled.VideoCodec == "h264" && scaled.AudioStreams == 1 && scaled.Duration is > 1.9 and < 2.2,
            "MP4 15fps / 320px 保持比例并混合双音轨", checks);
        Assert(await RecordingSession.GetExportSizeAsync(Path.Combine(run, "mp4-15-320.mp4")) == new Size(320, 180),
            "导出尺寸从实际编码文件读取", checks);

        var silent = await Export("mp4-24-original.mp4", false, true, new ExportOptions(24));
        Assert(silent.Width == 640 && silent.Height == 360 && Math.Abs(silent.FramesPerSecond - 24) < .01 && silent.AudioStreams == 0,
            "MP4 24fps / 原尺寸 / 静音", checks);
        var gif = await Export("gif-10-320.gif", true, false, new ExportOptions(10, 320));
        Assert(gif.Width == 320 && gif.Height == 180 && gif.Frames == 20 && gif.AudioStreams == 0 && Math.Abs(gif.FramesPerSecond - 10) < .1,
            "GIF 10fps / 320px 自动无音频且帧数正确", checks);
        Assert(await RecordingSession.GetExportSizeAsync(Path.Combine(run, "gif-10-320.gif")) == new Size(320, 180),
            "GIF 实际导出尺寸可读取", checks);

        var oversize = await Export("mp4-30-1920.mp4", false, false, new ExportOptions(30, 1920));
        Assert(oversize.Width == 640 && oversize.Height == 360 && oversize.FramesPerSecond == 30 && LastExportCopied(source),
            "最大宽度大于原图时不放大且匹配帧率直接复制视频流", checks);
        var oversizedGif = await Export("gif-10-1920.gif", true, true, new ExportOptions(10, 1920));
        Assert(oversizedGif.Width == 640 && oversizedGif.Height == 360,
            "GIF 最大宽度大于原图时不放大", checks);
        var odd = await Export("mp4-15-321.mp4", false, true, new ExportOptions(15, 321));
        Assert(odd.Width == 320 && odd.Height == 180 && odd.Width % 2 == 0 && odd.Height % 2 == 0,
            "MP4 奇数最大宽度向下取偶数且保持比例", checks);
        var legacy = await Export("legacy.mp4", false, false, null);
        Assert(legacy.Width == 640 && legacy.Height == 360 && legacy.FramesPerSecond == 30 && legacy.AudioStreams == 1 && LastExportCopied(source),
            "旧调用不传配置仍保留原始 MP4 视频流", checks);
        var legacyGif = await Export("legacy.gif", true, true, null);
        Assert(legacyGif.Width == 640 && legacyGif.Height == 360 && legacyGif.Frames == 24,
            "旧 GIF 调用保持 12fps 默认值", checks);

        foreach (var (isGif, invalid) in new[]
        {
            (false, new ExportOptions(0)), (false, new ExportOptions(61)), (true, new ExportOptions(31)),
            (false, new ExportOptions(20, 159)), (true, new ExportOptions(12, 7681))
        })
        {
            var rejected = false;
            try { await session.ExportAsync(Path.Combine(run, isGif ? "invalid.gif" : "invalid.mp4"), isGif, true, options: invalid); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, $"拒绝非法 {(isGif ? "GIF" : "MP4")} 配置 {invalid.FramesPerSecond}fps / {invalid.MaxWidth}px", checks);
        }

        // Failure after a successful conversion must preserve the previous destination and remove only our partial.
        var lockedPath = Path.Combine(run, "locked.mp4");
        File.Copy(Path.Combine(run, "legacy.mp4"), lockedPath);
        var previousHash = Hash(lockedPath);
        var failed = false;
        using (var destinationLock = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await session.ExportAsync(lockedPath, false, true, options: new ExportOptions(15, 320)); }
            catch (IOException) { failed = true; }
        }
        Assert(failed && Hash(lockedPath) == previousHash && !Directory.EnumerateFiles(run, "*.partial*", SearchOption.AllDirectories).Any(),
            "输出文件被占用时原输出不损坏且清理本次 partial", checks);
        var damagedSource = Path.Combine(run, "damaged-source");
        Directory.CreateDirectory(damagedSource);
        File.WriteAllBytes(Path.Combine(damagedSource, "capture.mkv"), new byte[2048]);
        using (var damaged = RecordingSession.Open(damagedSource))
        {
            failed = false;
            try { await damaged.ExportAsync(lockedPath, false, true, options: new ExportOptions(15, 320)); }
            catch (IOException) { failed = true; }
            Assert(failed && Hash(lockedPath) == previousHash &&
                !Directory.EnumerateFiles(run, "*.partial*", SearchOption.AllDirectories).Any() &&
                Directory.GetFiles(damagedSource, "export-*.log").Length == 1,
                "损坏原始视频导出失败时保留旧输出与诊断日志", checks);
        }
        Assert(Hash(capture) == originalHash && Hash(Path.Combine(source, "system.wav")) == originalSystem &&
            Hash(Path.Combine(source, "microphone.wav")) == originalMicrophone,
            "全部导出和失败重试均不修改原始视频与双音轨", checks);
        var logs = Directory.GetFiles(source, "export-*.log");
        Assert(logs.All(x => JsonDocument.Parse(File.ReadLines(x).First()).RootElement.TryGetProperty("FramesPerSecond", out _)),
            "每次导出日志记录实际配置及视频流是否复制", checks);

        async Task<ProbeInfo> Export(string name, bool isGif, bool mute, ExportOptions? options)
        {
            var path = Path.Combine(run, name);
            await session.ExportAsync(path, isGif, mute, options: options);
            var info = await ProbeAsync(path);
            details[name] = info;
            return info;
        }
    }

    private static bool LastExportCopied(string source)
    {
        var path = Directory.GetFiles(source, "export-*.log").OrderByDescending(Path.GetFileName).First();
        using var document = JsonDocument.Parse(File.ReadLines(path).First());
        return document.RootElement.GetProperty("CopyVideo").GetBoolean();
    }

    private static async Task<ProbeInfo> ProbeAsync(string path)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(RecordingSession.FfmpegPath)!, "ffprobe.exe");
        var text = await RunToolAsync(ffprobe, ["-v", "error", "-count_frames", "-show_streams", "-show_format", "-of", "json", path]);
        using var document = JsonDocument.Parse(text);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.First(x => x.GetProperty("codec_type").GetString() == "video");
        var rate = video.GetProperty("avg_frame_rate").GetString()!.Split('/');
        var fps = double.Parse(rate[0], CultureInfo.InvariantCulture) / double.Parse(rate[1], CultureInfo.InvariantCulture);
        var duration = double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        return new ProbeInfo(video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(), fps,
            int.Parse(video.GetProperty("nb_read_frames").GetString()!, CultureInfo.InvariantCulture), duration,
            video.GetProperty("codec_name").GetString()!, streams.Count(x => x.GetProperty("codec_type").GetString() == "audio"));
    }

    private static async Task<string> RunToolAsync(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动测试媒体工具。");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch { try { process.Kill(true); } catch { } throw; }
        var error = await errors;
        if (process.ExitCode != 0) throw new IOException(error);
        return await output;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Assert(bool condition, string description, List<string> checks)
    {
        if (!condition) throw new InvalidOperationException(description);
        checks.Add(description);
    }

    private sealed record ProbeInfo(int Width, int Height, double FramesPerSecond, int Frames, double Duration, string VideoCodec, int AudioStreams);
}
