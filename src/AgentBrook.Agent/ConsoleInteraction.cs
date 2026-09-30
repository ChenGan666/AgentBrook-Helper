using System.Text.Json;
using AgentBrook.Agent.Infrastructure;

namespace AgentBrook.Agent;

/// <summary>控制台宿主的 HITL 交互实现：终端提示 + ReadLine。</summary>
public sealed class ConsoleInteraction : IBrookInteraction
{
    /// <summary>完全访问模式：开启后所有审批自动通过（会话内有效）。</summary>
    public bool FullAccess { get; set; }

    public async Task<ApprovalDecision> GetApprovalAsync(string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
    {
        if (FullAccess)
        {
            Ui.Dim($"♾ 已自动批准（完全访问）：{toolName}");
            return ApprovalDecision.Approve();
        }
        Ui.Prompt($"\n🛡 需要审批：{toolName}");
        if (!string.IsNullOrEmpty(argumentsJson))
        {
            Ui.Prompt($"   参数：{argumentsJson}");
        }
        Ui.Prompt("批准执行吗？[y/N/a] a=完全访问（后续自动通过）；回车默认拒绝，也可输入拒绝理由");

        var answer = await ReadLineAsync(cancellationToken) ?? "";
        answer = answer.Trim();

        if (answer.Equals("a", StringComparison.OrdinalIgnoreCase))
        {
            FullAccess = true;
            Ui.Dim("   ♾ 已开启完全访问：本次及后续审批自动通过（重启后复位）");
            return ApprovalDecision.Approve();
        }
        var approved = answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                    || answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
        var reason = approved ? null : (answer.Length > 0 ? answer : "用户拒绝执行");
        Ui.Dim(approved ? "   ✔ 已批准，继续执行…" : $"   ✘ 已拒绝：{reason}");
        return new ApprovalDecision(approved, reason);
    }

    public async Task<string> AskUserAsync(string question, string? choices, CancellationToken cancellationToken = default)
    {
        Ui.Prompt($"\n❓ Brook 需要你的输入：{question}");
        if (!string.IsNullOrWhiteSpace(choices))
        {
            Ui.Dim($"   候选：{choices.Replace("|", " / ")}（也可自由回答）");
        }
        Ui.Prompt("你的回答 > ");

        var answer = await ReadLineAsync(cancellationToken);
        return answer?.Trim() ?? "";
    }

    /// <summary>带取消的 ReadLine（取消时返回 null，按默认拒绝处理）。</summary>
    private static async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var read = Task.Run(Console.ReadLine, ct);
        try
        {
            return await read.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
