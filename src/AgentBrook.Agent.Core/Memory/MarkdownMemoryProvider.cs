using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentBrook.Agent.Memory;

/// <summary>
/// 基于 Markdown 文件的长期记忆：
/// 每轮运行前把 memory 目录下的 .md 文件内容注入为附加指令（AIContext.Instructions），
/// 写入则由 <see cref="MemoryTools"/> 提供的模型可见工具完成。
/// </summary>
public sealed class MarkdownMemoryProvider(string memoryDir, int maxTotalChars) : AIContextProvider
{
    private readonly string _memoryDir = memoryDir;
    private readonly int _maxTotalChars = maxTotalChars;

    /// <summary>核心记忆文件，始终排首位。</summary>
    public const string CoreFileName = "MEMORY.md";

    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var memory = LoadAllMemory();
        if (string.IsNullOrEmpty(memory))
        {
            return new ValueTask<AIContext>(new AIContext());
        }

        return new ValueTask<AIContext>(new AIContext
        {
            Instructions =
                "以下是本智能体的长期记忆（来自 Markdown 记忆文件，每轮自动注入）。" +
                "其中与当前任务相关的信息应当被视为用户与项目的既定事实：\n\n" + memory,
        });
    }

    /// <summary>读取全部记忆文件（MEMORY.md 优先），超出总量上限时截断。</summary>
    public string LoadAllMemory()
    {
        if (!Directory.Exists(_memoryDir))
        {
            return string.Empty;
        }

        var files = Directory.GetFiles(_memoryDir, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(f => Path.GetFileName(f) != CoreFileName)   // MEMORY.md 排最前
            .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        var remaining = _maxTotalChars;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var content = ReadFileSafe(file);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var truncated = false;
            if (content.Length > remaining)
            {
                content = content[..remaining];
                truncated = true;
            }

            sb.AppendLine($"### memory/{name}").AppendLine(content.TrimEnd());
            if (truncated)
            {
                sb.AppendLine($"（… {name} 因超出注入上限被截断）");
                break;
            }
            remaining -= content.Length;
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>列出记忆文件名（供 /memory 命令与工具使用）。</summary>
    public IReadOnlyList<string> ListFiles()
    {
        return Directory.Exists(_memoryDir)
            ? Directory.GetFiles(_memoryDir, "*.md", SearchOption.TopDirectoryOnly)
                .Select(f => Path.GetFileName(f)!)
                .OrderBy(n => n != CoreFileName).ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
    }

    public string ReadFileSafe(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
