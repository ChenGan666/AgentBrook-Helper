using AgentBrook.Agent.Skills;

namespace AgentBrook.Agent;

/// <summary>智能体运行状态（用于宿主 UI 的状态展示）。</summary>
public enum BrookRunState
{
    Idle,
    Thinking,
    CallingTool,
    AwaitingApproval,
    AwaitingUserAnswer,
}

/// <summary>智能体对外抛出的流式事件（宿主据此渲染界面）。</summary>
public abstract record BrookEvent
{
    /// <summary>运行状态变化。</summary>
    public sealed record Status(BrookRunState State, string? Detail = null) : BrookEvent;

    /// <summary>助手文本增量。</summary>
    public sealed record TextDelta(string Text) : BrookEvent;

    /// <summary>模型发起一次工具调用（ask_user 亦由此事件先行为宿主所知）。</summary>
    public sealed record ToolStarted(string Name, string? ArgumentsJson) : BrookEvent;

    /// <summary>一次工具调用完成（ResultPreview 为结果前若干字符，供宿主展示执行反馈）。</summary>
    public sealed record ToolCompleted(string Name, string? ResultPreview) : BrookEvent;

    /// <summary>Token 用量（每回合结束发出；CumTotal 为应用累计总消耗）。</summary>
    public sealed record Usage(long TurnInput, long TurnOutput, long TurnTotal, long CumTotal) : BrookEvent;

    /// <summary>一个回合结束（最终回答已完整输出）。</summary>
    public sealed record TurnCompleted() : BrookEvent;

    /// <summary>回合内发生错误。</summary>
    public sealed record Failed(string Message) : BrookEvent;
}

/// <summary>工具目录条目。</summary>
public sealed record ToolInfo(string Name, string Source);

/// <summary>智能体静态信息快照（供宿主初始化界面）。</summary>
public sealed record BrookSnapshot(
    string Name,
    string CurrentModel,
    IReadOnlyList<string> Models,
    string WorkspaceRoot,
    IReadOnlyList<string> MemoryFiles,
    IReadOnlyList<SkillInfo> Skills,
    IReadOnlyList<ToolInfo> Tools,
    IReadOnlyList<string> ApprovalTools,
    IReadOnlyList<(string Server, int Tools)> McpServers);
