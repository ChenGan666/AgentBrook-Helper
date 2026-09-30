using System.ComponentModel;
using System.Text;
using AgentBrook.Agent.Infrastructure;

namespace AgentBrook.Agent.Tools;

/// <summary>工作区沙箱内的文件读写工具（所有路径都被限制在 workspace 根目录内）。</summary>
public sealed class FileTools(Workspace workspace, int maxOutputChars)
{
    private readonly Workspace _workspace = workspace;
    private readonly int _maxOutputChars = maxOutputChars;

    [Description("列出工作区内目录的内容。path 相对工作区根目录，默认列出根目录。")]
    public string list_dir([Description("目录相对路径，如 notes 或 .")] string path = ".")
    {
        var dir = _workspace.ResolveInside(path);
        if (!Directory.Exists(dir))
        {
            return $"目录不存在：{path}";
        }

        var sb = new StringBuilder();
        foreach (var d in Directory.GetDirectories(dir).Order(StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"[目录] {Path.GetFileName(d)}/");
        }
        foreach (var f in Directory.GetFiles(dir).Order(StringComparer.OrdinalIgnoreCase))
        {
            var size = new FileInfo(f).Length;
            sb.AppendLine($"[文件] {Path.GetFileName(f)}  ({size:N0} B)");
        }
        return sb.Length == 0 ? $"（{path} 为空目录）" : sb.ToString().TrimEnd();
    }

    // 已知二进制/媒体扩展名：读入会变成乱码洪水（曾把会话上下文撑爆到数百万 token），必须拒读
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".tiff",
        ".pdf", ".zip", ".gz", ".tar", ".rar", ".7z", ".dmg",
        ".mp3", ".mp4", ".mov", ".wav", ".aac", ".flac",
        ".docx", ".xlsx", ".pptx", ".exe", ".dll", ".dylib", ".so", ".bin", ".woff", ".woff2",
    };

    /// <summary>检测文件是否疑似二进制（前 512 字节出现 NUL 字节即判定）。</summary>
    private static bool LooksLikeBinary(string path)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[512];
        var read = fs.Read(buf, 0, buf.Length);
        return buf.Take(read).Any(b => b == 0);
    }

    [Description("读取工作区内的文本文件。超出长度上限时截断。图片等二进制文件无法读取。")]
    public async Task<string> read_file(
        [Description("文件相对路径，如 notes/todo.md")] string path,
        [Description("最多读取的字符数，默认 20000")] int max_chars = 20000)
    {
        var full = _workspace.ResolveInside(path);
        if (!File.Exists(full))
        {
            return $"文件不存在：{path}";
        }

        if (BinaryExtensions.Contains(Path.GetExtension(full)))
        {
            var size = new FileInfo(full).Length;
            var hint = Path.GetExtension(full).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp"
                ? "这是图片（二进制），文本工具无法读取其内容。当前模型不支持视觉输入——请告知用户：图片已妥善保存，如需查看图片请使用支持视觉的模型，或由用户描述图片内容。"
                : "这是二进制文件，文本工具无法读取其内容。";
            return $"（无法以文本读取：{path}，{size:N0} 字节。{hint}请勿再次尝试读取。）";
        }

        if (LooksLikeBinary(full))
        {
            return $"（无法以文本读取：{path} 是二进制文件。请勿再次尝试读取。）";
        }

        var content = await File.ReadAllTextAsync(full);
        if (content.Length > max_chars)
        {
            return content[..max_chars] + $"\n\n…（已截断：全文 {content.Length:N0} 字符，仅显示前 {max_chars:N0}）";
        }
        return content;
    }

    [Description("写入（或创建）工作区内的文本文件。目录不存在时自动创建；同名文件会被覆盖。")]
    public async Task<string> write_file(
        [Description("文件相对路径，如 notes/todo.md")] string path,
        [Description("要写入的完整文本内容")] string content)
    {
        var full = _workspace.ResolveInside(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, Encoding.UTF8);
        return $"已写入 {Path.GetRelativePath(_workspace.Root, full)}（{content.Length:N0} 字符）。";
    }

    [Description("在指定文件末尾追加文本（文件不存在时创建）。适合追加日志或要点。")]
    public async Task<string> append_file(
        [Description("文件相对路径")] string path,
        [Description("要追加的文本")] string content)
    {
        var full = _workspace.ResolveInside(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.AppendAllTextAsync(full, content, Encoding.UTF8);
        return $"已追加 {content.Length:N0} 字符到 {Path.GetRelativePath(_workspace.Root, full)}。";
    }
}
