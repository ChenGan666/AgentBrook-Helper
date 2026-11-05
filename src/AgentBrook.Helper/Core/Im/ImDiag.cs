using System.Text;

namespace AgentBrook.Helper.Core.Im;

/// <summary>
/// IM 诊断日志：同时写 stderr 与 **落盘**（VS 的"输出"窗口常不显示 stderr，导致用户看不到任何 [im]/[wx] 日志）。
/// 文件：&lt;workspace&gt;/im/host-log.txt。
/// 清理策略：**只保留最近 200 条**；同时硬上限 **128KB**——超限时从最旧的条目开始丢弃
/// （单条超长消息先截断为 30000 字符），任何写入完成后文件长度都不超过 128KB。
/// </summary>
internal static class ImDiag
{
    /// <summary>保留条数上限：最近 200 条。</summary>
    internal const int MaxEntries = 200;

    /// <summary>文件容量硬上限（字节）：128KB。</summary>
    internal const long MaxBytes = 128 * 1024;

    /// <summary>单条消息最大字符数（UTF-8 最坏约 120KB，保证单条也不会突破文件上限）。</summary>
    private const int MaxMessageChars = 30_000;

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
        try { Console.Error.WriteLine(message); } catch { /* 无控制台 */ }
        var path = _path;
        if (path is not null) LogTo(path, message);
    }

    /// <summary>写入指定日志文件（最近 200 条 + 128KB 上限）；独立参数便于测试。</summary>
    internal static void LogTo(string path, string message)
    {
        if (message.Length > MaxMessageChars) message = message[..MaxMessageChars] + "…（单条超长已截断）";
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var lines = ReadLines(path);
                var appendOnly = lines.Count < MaxEntries
                                 && ByteSize(lines) + LineBytes(line) <= MaxBytes;
                if (appendOnly)
                {
                    // 常规路径：只追加，不重写（外部查看器可同时读取）
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                    writer.Write(line);
                    writer.Write(Environment.NewLine);
                    return;
                }

                // 超限路径：加入本条后裁剪到最近 200 条，再从最旧丢弃直到 ≤128KB，原子重写
                lines.Add(line);
                if (lines.Count > MaxEntries) lines.RemoveRange(0, lines.Count - MaxEntries);
                while (lines.Count > 1 && ByteSize(lines) > MaxBytes) lines.RemoveAt(0);
                WriteLinesAtomic(path, lines);
            }
        }
        catch { /* 日志失败不影响功能 */ }
    }

    private static int LineBytes(string line) =>
        Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);

    private static long ByteSize(List<string> lines)
    {
        long total = 0;
        foreach (var line in lines) total += LineBytes(line);
        return total;
    }

    private static List<string> ReadLines(string path)
    {
        var lines = new List<string>();
        if (!File.Exists(path)) return lines;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    private static void WriteLinesAtomic(string path, List<string> lines)
    {
        var temp = path + ".tmp";
        File.WriteAllLines(temp, lines, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);   // 同卷原子替换，避免读到写了一半的文件
    }
}
