namespace AgentBrook.Runtime.Abstractions;

/// <summary>
/// 稳定错误码（契约冻结；ReasonCode/ErrorCategory 用）。字符串常量，随 JSON 以原样大小写序列化。
/// 语义对应设计 §4.2/§5/§11/§15；新增只追加不改名。
/// </summary>
public static class ImErrorCodes
{
    // ── 接收判定（ImAcceptResult.ReasonCode）──
    public const string NotPaired = "NotPaired";                 // 未配对（触发本机配对流程）
    public const string GroupDisabled = "GroupDisabled";         // 群/频道/Unknown 类型 P0 拒绝
    public const string QuotaExceeded = "QuotaExceeded";         // 附件数/大小/速率超限
    public const string BadPayload = "BadPayload";               // 协议鉴权/必填/大小声明不合法
    public const string IdentityMismatch = "IdentityMismatch";   // 消息身份与 Sink 绑定不一致
    public const string RateLimited = "RateLimited";             // 主体接收速率/熔断

    // ── 背压 ──
    public const string Busy = "Busy";                           // Inbox 满/库不可写/Agent 未就绪（未接收）

    // ── 授权与代次 ──
    public const string Unauthorized = "Unauthorized";
    public const string AccountMismatch = "AccountMismatch";     // AccountEpoch 失配
    public const string AuthorizationRevoked = "AuthorizationRevoked"; // AuthorizationEpoch 失配
    public const string GenerationStale = "GenerationStale";     // 会话已删除/旧 generation
    public const string RevisionConflict = "RevisionConflict";   // 快照 CAS 失败（须重载最新）

    // ── 契约与宿主 ──
    public const string ContractVersionMissing = "ContractVersionMissing";   // 宿主缺扩展，初始化前拒绝
    public const string ContractVersionMismatch = "ContractVersionMismatch"; // 版本不兼容，加载/初始化前拒绝
    public const string ServiceNotGranted = "ServiceNotGranted";             // 未授予的宿主服务
    public const string ActivationStale = "ActivationStale";                 // 旧活动实例句柄（停启/更新后）
    public const string StorageUnavailable = "StorageUnavailable";           // 状态记录不可用（写前标记失败等）
    public const string RecoveryRequired = "RecoveryRequired";               // 未知副作用，需人工核对

    // ── 投递 ──
    public const string Temporary = "Temporary";                 // 临时失败（可重试）
    public const string Permanent = "Permanent";                 // 永久失败
    public const string DeadlineExceeded = "DeadlineExceeded";   // 任务有效期已过（转人工确认）
    public const string CancelPending = "CancelPending";         // 取消意图已持久化、远端未确认（取消待确认）
}
