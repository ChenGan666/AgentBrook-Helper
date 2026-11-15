namespace AgentBrook.Helper.Core.Voice;

/// <summary>
/// 语音链路诊断日志（doc/Voice/06）：workspace/voice/voice-log.txt，带时间戳逐行追加，
/// 超 256KB 截断保留后半。只记尺寸/阶段/错误码，不落音频内容（脱敏红线）。
/// Windows 打包版 stdout 不可见，此文件是远程排障的唯一窗口。
/// </summary>
internal static class VoiceLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 256 * 1024;

    public static void Write(string message)
    {
        try
        {
            var dir = Path.Combine(Helper.Support.UiPrefs.WorkspaceDir(), "voice");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "voice-log.txt");
            lock (Gate)
            {
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                {
                    var old = File.ReadAllBytes(file);
                    var keep = new byte[MaxBytes / 2];
                    Buffer.BlockCopy(old, old.Length - keep.Length, keep, 0, keep.Length);
                    File.WriteAllBytes(file, keep);
                }
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
                File.AppendAllText(file, line);
            }
            Console.Error.WriteLine($"[voice] {message}");
        }
        catch
        {
            // 日志失败不影响功能
        }
    }
}
