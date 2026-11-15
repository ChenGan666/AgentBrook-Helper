namespace AgentBrook.Plugin.Tts;

/// <summary>
/// 插件侧落盘日志（VoiceLog 同款纪律，doc/Voice/07 §5.8）：workspace/voice/tts-log.txt。
/// 隐私红线：只记尺寸/阶段/耗时/错误码，绝不记播报文本与用户原文；超 256KB 截断留后半。
/// </summary>
internal static class TtsLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Bind(string workspaceRoot)
    {
        var dir = Path.Combine(workspaceRoot, "voice");
        try
        {
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "tts-log.txt");
        }
        catch
        {
            _path = null;   // 目录不可写则静默（日志失败不影响功能）
        }
    }

    public static void Write(string message)
    {
        if (_path is null) return;
        lock (Gate)
        {
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 256 * 1024)
                {
                    var bytes = File.ReadAllBytes(_path);
                    File.WriteAllBytes(_path, bytes[(bytes.Length / 2)..]);
                }
                File.AppendAllText(_path, line);
            }
            catch
            {
                // 日志失败静默（不干扰主流程）
            }
        }
    }
}
