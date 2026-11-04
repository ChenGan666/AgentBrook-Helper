namespace AgentBrook.Messaging.Abstractions;

/// <summary>Outbox 行状态（设计 §10.1 v4.2 语义）。AcceptedByProvider 与 Sent 必须区分——受理 ≠ 平台成功。</summary>
public enum OutboxState
{
    Pending = 1,
    AcceptedByProvider = 2,
    Sent = 3,
    Unknown = 4,
    FailedTemporary = 5,   // 临时失败：有界自动重试（经 Pending 语义重排）
    FailedPermanent = 6,   // 永久失败：仅人工
    Cancelled = 7,
}

/// <summary>
/// Outbox 状态机（设计 §10.1"状态只允许合法迁移"的契约冻结表；C04 参数化全量校验）。
/// 规则：
///  - 除 FailedTemporary → Pending 外，任何状态不得回退 Pending（查询超时/404/迟到响应不得触发重发）；
///  - 终态（Sent/FailedPermanent/Cancelled）不可迁出；迟到/乱序响应不得覆盖更终态；
///  - Unknown 只能前进：查询得 sent→Sent、accepted→AcceptedByProvider、failed→FailedPermanent、cancel 成功→Cancelled。
/// </summary>
public static class OutboxStateMachine
{
    private static readonly Dictionary<OutboxState, IReadOnlySet<OutboxState>> Transitions = new()
    {
        [OutboxState.Pending] = new HashSet<OutboxState>
        {
            OutboxState.AcceptedByProvider, OutboxState.Sent, OutboxState.Unknown,
            OutboxState.FailedTemporary, OutboxState.FailedPermanent, OutboxState.Cancelled,
        },
        [OutboxState.AcceptedByProvider] = new HashSet<OutboxState>
        {
            OutboxState.Sent, OutboxState.Unknown, OutboxState.FailedPermanent, OutboxState.Cancelled,
        },
        [OutboxState.Unknown] = new HashSet<OutboxState>
        {
            OutboxState.Sent, OutboxState.AcceptedByProvider, OutboxState.FailedPermanent, OutboxState.Cancelled,
        },
        [OutboxState.FailedTemporary] = new HashSet<OutboxState>
        {
            OutboxState.Pending, OutboxState.Cancelled,
        },
        [OutboxState.Sent] = new HashSet<OutboxState>(),
        [OutboxState.FailedPermanent] = new HashSet<OutboxState>(),
        [OutboxState.Cancelled] = new HashSet<OutboxState>(),
    };

    public static bool CanTransition(OutboxState from, OutboxState to) =>
        from == to || Transitions[from].Contains(to);

    /// <summary>尝试迁移；非法迁移返回 false（调用方保持原状态并记录，不得抛异常破坏账务事务）。</summary>
    public static bool TryTransition(ref OutboxState current, OutboxState target)
    {
        if (!CanTransition(current, target)) return false;
        current = target;
        return true;
    }

    public static bool IsTerminal(OutboxState state) => Transitions[state].Count == 0;

    /// <summary>无确定终态 = 取消范围（v4.3）：含 Unknown（受理响应丢失）与在途 Pending/AcceptedByProvider。</summary>
    public static bool IsCancellable(OutboxState state) =>
        state is OutboxState.Pending or OutboxState.AcceptedByProvider or OutboxState.Unknown
            or OutboxState.FailedTemporary;
}
