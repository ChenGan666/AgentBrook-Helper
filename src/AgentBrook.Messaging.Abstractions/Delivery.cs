using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Messaging.Abstractions;

/// <summary>
/// 出站投递请求（设计 §4.2）：Outbox 分片粒度；持久化 DTO 规则——不含流/委托/宿主绝对路径，
/// 文件只经宿主签发 ContentRef（不经绝对路径旁路）。本地 DeliveryId 不代表平台具备幂等能力。
/// </summary>
public sealed record DeliveryRequest(
    string DeliveryId,              // 本地生成（Outbox 分片粒度）
    long AccountEpoch,              // 目标账号代次（发送前核对，§11.6）
    string AccountId,               // 目标账号（受理方发送前核对仍一致）
    string ChatId,
    string? Text,
    IReadOnlyList<ContentRef>? Files,
    string? IdempotencyKey,         // 平台支持时携带（桥=true，TG=false）
    DateTimeOffset? Deadline)       // 任务有效期（与受理方幂等保留期对齐）
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(DeliveryId) &&
        !string.IsNullOrWhiteSpace(AccountId) &&
        !string.IsNullOrWhiteSpace(ChatId) &&
        (Text is { Length: > 0 } || Files is { Count: > 0 }) &&
        (Files is null || Files.All(f => f.IsValid));
}

/// <summary>投递受理结果（设计 §4.2）。语义：平台接受 ≠ 用户已读；未知 ≠ 失败。</summary>
public sealed record DeliveryResult(
    DeliveryState State,
    string? PlatformMessageId,   // PlatformAccepted 时
    TimeSpan? RetryAfter,        // 平台明确限流提示（如 429 retry_after）
    string? ErrorCategory,       // ImErrorCodes：Temporary | Permanent | Unauthorized | Busy …
    string? Message)
{
    public static DeliveryResult Accepted() => new(DeliveryState.Accepted, null, null, null, null);
    public static DeliveryResult PlatformAccepted(string platformMessageId) => new(DeliveryState.PlatformAccepted, platformMessageId, null, null, null);
    public static DeliveryResult Unknown(string message) => new(DeliveryState.Unknown, null, null, null, message);
    public static DeliveryResult Rejected(string errorCategory, string message) => new(DeliveryState.Rejected, null, null, errorCategory, message);
}

public enum DeliveryState
{
    /// <summary>已持久化受理（桥）；最终发送责任在受理方（§10.1 责任交接）。</summary>
    Accepted = 1,
    /// <summary>平台已接受。</summary>
    PlatformAccepted = 2,
    /// <summary>明确拒绝（未受理）。</summary>
    Rejected = 3,
    /// <summary>结果未知（超时/连接中断）；按幂等键能力决定可否自动重试。</summary>
    Unknown = 4,
}

/// <summary>投递跟踪终态（设计 §4.5，v4.3 加 Cancelled）。NotFound=超出保留期/未知（走人工确认，不回退 Pending）。</summary>
public enum DeliveryTrackingState
{
    AcceptedByProvider = 1,
    Sent = 2,
    Unknown = 3,
    Failed = 4,
    Cancelled = 5,
    NotFound = 6,
}

/// <summary>待发取消三态（v4.3）：不无条件报成功。</summary>
public enum DeliveryCancelState
{
    /// <summary>已阻止（未发送且不会再发送，含"受理方尚未见过该 ID 但已立墓碑"）。</summary>
    Cancelled = 1,
    /// <summary>开始平台发送后无法召回（在途消息如实上报）。</summary>
    AlreadyDispatched = 2,
    /// <summary>状态不明（按查询兜底）。</summary>
    Unknown = 3,
}

/// <summary>支持最终状态查询的通道插件实现（桥=true；Telegram 不实现 → 查询能力缺失时 UI 明示）。</summary>
public interface IDeliveryStatusProvider
{
    /// <summary>查询已提交投递的最终状态。临时失败抛错由调用方按退避重查；NotFound 走人工确认路径。</summary>
    Task<DeliveryTrackingState> QueryAsync(string deliveryId, CancellationToken cancellationToken);
}

/// <summary>支持待发取消的通道插件实现（桥=true）。幂等；取消范围含无确定终态的全部已提交投递（v4.3）。</summary>
public interface IDeliveryCancellationProvider
{
    Task<DeliveryCancelState> CancelAsync(string deliveryId, CancellationToken cancellationToken);
}
