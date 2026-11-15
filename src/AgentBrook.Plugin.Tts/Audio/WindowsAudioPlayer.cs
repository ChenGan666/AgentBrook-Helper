using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AgentBrook.Plugin.Tts.Audio;

/// <summary>
/// Windows 播放端：NAudio WasapiOut（共享模式，默认输出设备，自动重采样任意格式）。
/// 音量 = WaveOutEvent/WasapiOut Volume 单路径（doc/Voice/07 §5.4 C4：不做 PCM 缩放）。
/// 仅在 OperatingSystem.IsWindows() 下实例化（NAudio 程序集为托管代码，mac 上仅编译不加载该类型）。
/// </summary>
public sealed class WindowsAudioPlayer : IAudioPlayer
{
    private WasapiOut? _output;
    private readonly object _gate = new();
    private volatile bool _disposed;

    public async Task<(bool Completed, string? ErrorCode)> PlayAsync(string wavPath, float volume, CancellationToken ct)
    {
        if (_disposed) return (false, "tts.disposed");
        try
        {
            return await Task.Run<(bool, string?)>(() =>
            {
                lock (_gate)
                {
                    if (_disposed) return (false, "tts.disposed");
                    using var reader = new AudioFileReader(wavPath);
                    var output = new WasapiOut(AudioClientShareMode.Shared, 100);
                    _output = output;
                    output.Volume = Math.Clamp(volume, 0f, 1f);
                    output.Init(reader);

                    var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    output.PlaybackStopped += (_, _) => stopped.TrySetResult(true);
                    output.Play();

                    while (true)
                    {
                        if (_disposed) { output.Stop(); return (false, "tts.disposed"); }
                        if (ct.IsCancellationRequested) { output.Stop(); return (false, "tts.cancelled"); }
                        if (stopped.Task.IsCompleted) break;
                        Task.Delay(100, CancellationToken.None).Wait(CancellationToken.None);
                    }
                    return (stopped.Task.Result, null);
                }
            }, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return (false, "tts.cancelled");
        }
        catch (Exception)
        {
            return (false, "tts.play.device");   // 设备不可用/格式失败
        }
        finally
        {
            lock (_gate) { _output = null; }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _output?.Stop(); } catch { /* 未初始化/已停止忽略 */ }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            try { _output?.Stop(); _output?.Dispose(); } catch { }
            _output = null;
        }
    }
}
