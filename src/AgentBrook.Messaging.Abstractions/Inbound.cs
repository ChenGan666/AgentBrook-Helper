using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Messaging.Abstractions;

/// <summary>规范化聊天类型（设计 §4.3）：适配器映射平台值，无法映射 → Unknown。Group/Channel/Unknown P0 一律拒绝。</summary>
public enum ImChatType
{
    Private = 1,
    Group = 2,
    Channel = 3,
    Unknown = 4,
}

public enum ImAttachmentKind
{
    File = 1,
    Image = 2,
    Voice = 3,
    Video = 4,
}

/// <summary>附件物化状态（设计 §8.1）：随 Inbox 行更新，插件不直接改库。</summary>
public enum ImAttachmentState
{
    Referenced = 1,
    Materialized = 2,
    Expired = 3,
    Rejected = 4,
    BlockedByProvider = 5,
}

/// <summary>
/// 附件描述符（设计 §8.1）：纯数据、可序列化（替代懒加载委托——委托永不持久化）。
/// Provider 是物化器注册键（如 "telegram-file"/"bridge-blob"），含实例命名空间。
/// </summary>
public sealed record ImAttachmentDescriptor(
    string Provider,
    int SchemaVersion,
    string RemoteId,
    string? Name,
    string? MediaType,
    ImAttachmentKind Kind,
    long? DeclaredBytes,
    DateTimeOffset? ExpiresAt,
    ImAttachmentState State,
    string? LocalBlobId)   // Materialized 后指向宿主 Blob（生成 ID）
{
    /// <summary>
    /// 转写记录（doc/Voice/01 B2，additive）：平台文本轨（Engine="platform"）或宿主 ASR 回写。
    /// 非位置 init 属性——10 参位置构造保持不动，防止 Telegram/Weixin/Bridge 三插件
    /// 源码级与二进制级双重破坏；旧 attachments_json 缺字段反序列化为 null（向后兼容）。
    /// </summary>
    public ImAttachmentTranscript? Transcript { get; init; }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Provider) &&
        SchemaVersion > 0 &&
        !string.IsNullOrWhiteSpace(RemoteId) &&
        (State != ImAttachmentState.Materialized || !string.IsNullOrWhiteSpace(LocalBlobId));
}

/// <summary>
/// 归一化入站消息（设计 §4.3）。可序列化 DTO；无流/委托/绝对路径。
/// Timestamp 一律 UTC。
/// </summary>
public sealed record ImInboundMessage(
    string ChannelId,
    string ChannelInstanceId,
    string AccountId,
    string ChatId,
    ImChatType ChatType,
    string SenderId,
    string? SenderName,
    bool IsSelf,
    bool IsBot,
    string Text,
    string MessageId,
    DateTimeOffset Timestamp,
    IReadOnlyList<ImAttachmentDescriptor>? Attachments)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ChannelId) &&
        !string.IsNullOrWhiteSpace(ChannelInstanceId) &&
        !string.IsNullOrWhiteSpace(AccountId) &&
        !string.IsNullOrWhiteSpace(ChatId) &&
        !string.IsNullOrWhiteSpace(SenderId) &&
        !string.IsNullOrWhiteSpace(MessageId);
}

/// <summary>接收判定（设计 §4.2）：Accepted/Duplicate/Rejected=已决（通道可确认平台）；Busy=未接收（背压暂停，RetryAfter 必填）。</summary>
public sealed record ImAcceptResult(
    ImAcceptState State,
    string? ReasonCode,     // ImErrorCodes：NotPaired / GroupDisabled / QuotaExceeded / BadPayload / IdentityMismatch / RateLimited / Busy
    TimeSpan? RetryAfter)
{
    public static ImAcceptResult Accepted() => new(ImAcceptState.Accepted, null, null);
    public static ImAcceptResult Duplicate() => new(ImAcceptState.Duplicate, null, null);
    public static ImAcceptResult Rejected(string reasonCode) => new(ImAcceptState.Rejected, reasonCode, null);
    public static ImAcceptResult Busy(TimeSpan retryAfter) => new(ImAcceptState.Busy, ImErrorCodes.Busy, retryAfter);

    /// <summary>已决 = 通道可据此推进平台确认游标（Telegram 连续已决前缀语义）。</summary>
    public bool IsDecided => State != ImAcceptState.Busy;
}

public enum ImAcceptState
{
    Accepted = 1,
    Duplicate = 2,
    Rejected = 3,
    Busy = 4,
}

/// <summary>
/// 可靠接收的唯一入口（设计 §4.1）：核心按"通道实例"创建并注入（非全局单例）。
/// Identity 由宿主签发随句柄只读携带；消息中的 ChannelId/InstanceId 由宿主按 Identity
/// 归一校验，不一致即 Rejected（IdentityMismatch）——防误配置、旧实例迟到提交、账号代次错位。
/// </summary>
public interface IImInboundSink
{
    ImEndpointIdentity Identity { get; }

    /// <summary>持久化 Inbox 并返回判定（幂等键 hash(PluginId|InstanceId|AccountId|ChatId|MessageId)）。</summary>
    Task<ImAcceptResult> AcceptInboundAsync(ImInboundMessage message, CancellationToken cancellationToken);
}
