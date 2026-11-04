using System.Text;

namespace AgentBrook.Helper.Core.Im;

/// <summary>
/// IM 诊断日志：同时写 stderr 与 **落盘**（VS 的"输出"窗口常不显示 stderr，导致用户看不到任何 [im]/[wx] 日志）。
/// 文件：&lt;workspace&gt;/im/host-log.txt（超过 2MB 自动裁剪保留后半段）。
/// </summary>
internal static class ImDiag
{
    private static readonly object Gate = new();
    private static string? _path;

    /// <summary>诊断日志路径（未初始化时为 null）。</summary>
    internal static string? FilePath => _path;

    internal static void Init(string workspaceRoot)
    {
        // 只记录路径，**不建目录**：IM 关闭时必须零副作用（U01）；目录在首次落盘时创建
        try { _path = Path.Combine(workspaceRoot, "im", "host-log.txt"); }
        catch { _path = null; }
    }

    internal static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        try { Console.Error.WriteLine(message); } catch { /* 无控制台 */ }
        var path = _path;
        if (path is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                var info = new FileInfo(path);
                if (info.Length > 2 * 1024 * 1024)
                {
                    var lines = File.ReadAllLines(path);
                    File.WriteAllLines(path, lines.Skip(lines.Length / 2), new UTF8Encoding(false));
                }
            }
        }
        catch { /* 日志失败不影响功能 */ }
    }
}
