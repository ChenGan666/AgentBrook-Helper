namespace AgentBrook.Agent;

/// <summary>一次工具审批的人工决定。</summary>
public readonly record struct ApprovalDecision(bool Approved, string? Reason)
{
    public static ApprovalDecision Approve() => new(true, null);
    public static ApprovalDecision Reject(string? reason = null) => new(false, reason ?? "用户拒绝执行");
}

/// <summary>
/// HITL 交互抽象：核心库在需要人工审批、需要用户回答时回调宿主实现，
/// 宿主决定呈现方式（控制台提示、Web 卡片、语音播报等），挂起等待用户输入。
/// </summary>
public interface IBrookInteraction
{
    /// <summary>请求用户批准一次工具调用。返回批准与否（拒绝可附理由，理由会回传给模型）。</summary>
    Task<ApprovalDecision> GetApprovalAsync(string toolName, string? argumentsJson, CancellationToken cancellationToken = default);

    /// <summary>任务执行中向用户提问并等待回答（ask_user 工具）。</summary>
    Task<string> AskUserAsync(string question, string? choices, CancellationToken cancellationToken = default);
}
