using System.Diagnostics;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Runtime;

/// <summary>
/// 非安全工具写前标记包装器（设计 §11.4）：执行任何非安全工具前必须先把标记落盘，
/// 落盘失败 → 该工具调用直接失败（外部调用计数必须为 0，§6.3 可量化判定）。
/// 由工具执行包装器强制，不依赖模型自觉；本地/IM 回合统一经此（经协调器的回合）。
/// </summary>
public sealed class UnsafeToolGate(CommitJournal journal)
{
    /// <summary>
    /// 受控执行非安全工具：标记落盘 → 执行 → 返回。任何失败路径都保证工具未执行。
    /// </summary>
    public async Task<T> ExecuteUnsafeToolAsync<T>(
        TurnExecutionContext context,
        string toolName,
        string detailJson,
        Func<CancellationToken, Task<T>> tool,
        CancellationToken cancellationToken)
    {
        ImFaultPoints.Raise(ImFaultPoints.UnsafeBeforeMark);
        var marked = await journal.MarkUnsafeAttemptAsync(context.TurnId, detailJson).ConfigureAwait(false);
        if (!marked)
            throw new RuntimePipelineException(ImErrorCodes.StorageUnavailable,
                $"非安全工具 {toolName} 的写前标记落盘失败——禁止执行（状态记录不可用）。");

        ImFaultPoints.Raise(ImFaultPoints.UnsafeAfterMark);
        var result = await tool(cancellationToken).ConfigureAwait(false);
        ImFaultPoints.Raise(ImFaultPoints.UnsafeAfterTool);
        return result;
    }

    /// <summary>无返回值便捷重载。</summary>
    public Task ExecuteUnsafeToolAsync(
        TurnExecutionContext context, string toolName, string detailJson,
        Func<CancellationToken, Task> tool, CancellationToken cancellationToken) =>
        ExecuteUnsafeToolAsync<object?>(context, toolName, detailJson,
            async ct => { await tool(ct).ConfigureAwait(false); return null; }, cancellationToken);
}

/// <summary>
/// 外部副作用记录器（计划 §8：与可能被终止的进程分离——落地为文件 JSONL，恢复进程/父进程可读）。
/// F 表断言以本记录器计数为准，不把计数放进被杀进程内存。
/// </summary>
public sealed class ExternalEffectRecorder(string filePath)
{
    private readonly object _gate = new();

    public void Record(string kind, string id, string? detail = null)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var line = ImJson.Serialize(new { kind, id, detail, at = DateTimeOffset.UtcNow.ToString("O") });
            File.AppendAllText(filePath, line + "\n");
        }
    }

    public IReadOnlyList<(string Kind, string Id)> ReadAll()
    {
        if (!File.Exists(filePath)) return [];
        lock (_gate)
        {
            return [.. File.ReadLines(filePath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(l);
                    return (doc.RootElement.GetProperty("kind").GetString()!,
                            doc.RootElement.GetProperty("id").GetString()!);
                })];
        }
    }

    public int Count(string kind, string? id = null) => id is null
        ? ReadAll().Count(c => c.Kind == kind)
        : ReadAll().Count(c => c.Kind == kind && c.Id == id);
}
