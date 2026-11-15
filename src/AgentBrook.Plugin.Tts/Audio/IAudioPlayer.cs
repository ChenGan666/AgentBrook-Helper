namespace AgentBrook.Plugin.Tts.Audio;

/// <summary>
/// 音频播放抽象（doc/Voice/07 §5.4：合成/播放逻辑分离；音量单路径 = 播放器 Volume）。
/// 实现：Windows = NAudio WasapiOut；macOS = AVAudioPlayer ObjC interop。
/// </summary>
public interface IAudioPlayer
{
    /// <summary>播放 wav 文件直至结束/停止/取消。返回 true=自然播完；false=被打断或设备失败（Details 带错误码）。</summary>
    Task<(bool Completed, string? ErrorCode)> PlayAsync(string wavPath, float volume, CancellationToken ct);

    /// <summary>停止当前播放（幂等；无播放也安全）。</summary>
    void Stop();

    /// <summary>释放底层资源（ShutdownAsync 调用）。</summary>
    void Dispose();
}
