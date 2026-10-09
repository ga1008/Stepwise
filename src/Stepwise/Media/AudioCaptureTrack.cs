using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Stepwise.Media;

/// <summary>Places samples on a shared wall-clock timeline, including silent gaps from WASAPI loopback.</summary>
internal sealed class AudioCaptureTrack
{
    private readonly object _sync = new();
    private readonly WasapiCapture _capture;
    private readonly WaveFileWriter _writer;
    private readonly Stopwatch _clock;
    private readonly Action<string> _warning;
    private readonly string _label;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] _silence;
    private readonly int _bytesPerSecond;
    private readonly int _alignment;
    private bool _accepting = true;
    private bool _finished;
    private bool _failed;
    private double _lastFlush;

    private AudioCaptureTrack(string path, string label, bool loopback, Stopwatch clock, Action<string> warning)
    {
        _label = label;
        _clock = clock;
        _warning = warning;
        if (!loopback)
        {
            // Device state is informational only: never change the user's mute or Windows privacy settings.
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            if (device.AudioEndpointVolume.Mute)
                warning("麦克风当前处于系统静音状态；取消静音后才会收音。");
        }
        _capture = loopback ? new WasapiLoopbackCapture() : new WasapiCapture();
        try
        {
            _writer = new WaveFileWriter(path, _capture.WaveFormat);
            _bytesPerSecond = _capture.WaveFormat.AverageBytesPerSecond;
            _alignment = _capture.WaveFormat.BlockAlign;
            _silence = new byte[_bytesPerSecond];
            if (_capture.WaveFormat.BitsPerSample == 8) Array.Fill(_silence, (byte)128);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
        }
        catch { _capture.Dispose(); throw; }
    }

    public static AudioCaptureTrack Start(string path, string label, bool loopback, Stopwatch clock, Action<string> warning)
    {
        var track = new AudioCaptureTrack(path, label, loopback, clock, warning);
        try { track._capture.StartRecording(); return track; }
        catch
        {
            track._accepting = false;
            track._capture.Dispose();
            track._writer.Dispose();
            throw;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        lock (_sync)
        {
            if (!_accepting || _failed || args.BytesRecorded == 0) return;
            try
            {
                var count = args.BytesRecorded / _alignment * _alignment;
                var expectedStart = Math.Max(0, _clock.Elapsed.TotalSeconds - count / (double)_bytesPerSecond);
                var writtenSeconds = _writer.Length / (double)_bytesPerSecond;
                // Keep uninterrupted samples contiguous; repair genuine silent intervals without adding callback jitter.
                if (expectedStart - writtenSeconds > .12 || _writer.Length == 0) PadTo(expectedStart);
                _writer.Write(args.Buffer, 0, count);
                if (_clock.Elapsed.TotalSeconds - _lastFlush >= 2)
                {
                    // WAV headers otherwise remain empty until Dispose; checkpoint for crash recovery.
                    _writer.Flush();
                    _lastFlush = _clock.Elapsed.TotalSeconds;
                }
                if (_writer.Length > 3_900_000_000L)
                    throw new IOException("此音轨接近 WAV 容量上限，画面将继续录制。请结束当前步骤后重新录制。");
            }
            catch (Exception e)
            {
                _failed = true;
                _warning($"{_label}录制中断：{e.Message}");
                // Stop outside this callback so a capture-thread join cannot deadlock.
                _ = Task.Run(() => { try { _capture.StopRecording(); } catch { } });
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null) _warning($"{_label}已停止：{args.Exception.Message}");
        _stopped.TrySetResult();
    }

    public async Task StopAsync(double stopAt)
    {
        if (_finished) return;
        try
        {
            _capture.StopRecording();
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception e) { _warning($"{_label}停止时出现问题，已保留音轨：{e.Message}"); }
        finally
        {
            lock (_sync) _accepting = false;
            var disposeDevice = Task.Run(() =>
            {
                try { _capture.Dispose(); } catch (Exception e) { _warning($"{_label}设备释放失败：{e.Message}"); }
            });
            try { await disposeDevice.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { _warning($"{_label}驱动响应较慢，音轨已停止写入并保留。"); }
            lock (_sync)
            {
                if (!_finished)
                {
                    try { if (!_failed) PadTo(stopAt); }
                    catch (Exception e) { _warning($"{_label}末尾静音补齐失败：{e.Message}"); }
                    finally
                    {
                        try { _writer.Dispose(); } catch (Exception e) { _warning($"{_label}文件收尾失败：{e.Message}"); }
                        _finished = true;
                    }
                }
            }
        }
    }

    private void PadTo(double seconds)
    {
        var target = Math.Min(3_900_000_000L, (long)(seconds * _bytesPerSecond)) / _alignment * _alignment;
        while (_writer.Length < target)
        {
            var count = (int)Math.Min(_silence.Length, target - _writer.Length);
            _writer.Write(_silence, 0, count);
        }
    }
}
