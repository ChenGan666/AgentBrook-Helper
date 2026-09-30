using System.ComponentModel;
using System.Text;
using AgentBrook.Agent.Infrastructure;
using AgentBrook.Agent.Memory;

namespace AgentBrook.Agent.Tools;

/// <summary>
/// 模型可见的记忆维护工具：读取、覆写、追加 Markdown 记忆文件。
/// 只允许操作 memory 目录下的 .md 文件。
/// </summary>
public sealed class MemoryTools
{
    private readonly MarkdownMemoryProvider _provider;
    private readonly string _memoryDir;

    public MemoryTools(Workspace workspace, MarkdownMemoryProvider provider)
    {
        _memoryDir = workspace.MemoryDir;
        _provider = provider;
    }

    [Description("读取智能体的全部 Markdown 长期记忆内容。")]
    public string memory_read()
    {
        var content = _provider.LoadAllMemory();
        return string.IsNullOrEmpty(content) ? "（记忆为空）" : content;
    }

    [Description("覆写一个记忆文件的内容（.md 后缀自动补全）。用于整理、重写记忆。")]
    public string memory_write(
        [Description("记忆文件名，如 MEMORY.md 或 project-notes.md")] string filename,
        [Description("完整的 Markdown 内容（将整体覆盖原文件）")] string content)
    {
        var path = SafePath(filename);
        File.WriteAllText(path, content, Encoding.UTF8);
        return $"已写入 memory/{Path.GetFileName(path)}（{content.Length} 字符）。";
    }

    [Description("向记忆文件追加一条要点（自动加时间戳）。用于记录用户偏好、项目事实、经验教训等值得长期记住的信息。")]
    public async Task<string> memory_append(
        [Description("记忆文件名，通常为 MEMORY.md")] string filename,
        [Description("要追加的一行要点")] string note)
    {
        var path = SafePath(filename);
        var line = $"- [{DateTime.Now:yyyy-MM-dd HH:mm}] {note.Trim()}";
        await File.AppendAllTextAsync(path, line + Environment.NewLine, Encoding.UTF8);
        return $"已追加到 memory/{Path.GetFileName(path)}。";
    }

    private string SafePath(string filename)
    {
        var name = Path.GetFileName(filename.Trim());
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("文件名不能为空。");
        }
        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            name += ".md";
        }
        return Path.Combine(_memoryDir, name);
    }
}
