namespace AgentBrook.Runtime.Abstractions;

/// <summary>回合来源（设计 §7）：IM 来源必有 ReplyTarget；本地来源为空 → 不外发。</summary>
public enum TurnOrigin
{
    Local = 1,
    Im = 2,
}

/// <summary>IM 回执目标（chat 绑定）。持久化 DTO：无流/委托/绝对路径。</summary>
public sealed record ReplyTarget(string ChannelId, string InstanceId, string AccountId, string ChatId)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ChannelId) &&
        !string.IsNullOrWhiteSpace(InstanceId) &&
        !string.IsNullOrWhiteSpace(AccountId) &&
        !string.IsNullOrWhiteSpace(ChatId);
}

/// <summary>
/// 工具与数据权限档（设计 §13 权限矩阵的 P0 形态）。由宿主按调用链注入，
/// 插件 DTO 不可自填（双层授权 §15.2）。
/// </summary>
public sealed record PermissionPolicy(
    bool SandboxReadWrite,          // list_dir/read_file/write_file：IM 沙箱根
    bool CommandRequiresApproval,   // run_command：必须审批（5 分钟超时=拒绝）
    bool RemoteMemoryReadWrite,     // 独立远程记忆空间 workspace/im/memory/
    bool FileSendRequiresApproval,  // im_send_file：首次外发每文件审批
    IReadOnlyList<string> DisabledTools) // P0 关闭清单（ask_user/subAgent_*/team_* 等）
{
    public bool IsToolEnabled(string toolName) => !DisabledTools.Contains(toolName);

    /// <summary>P0 私聊默认档（§13 矩阵）。</summary>
    public static PermissionPolicy P0Default { get; } = new(
        SandboxReadWrite: true,
        CommandRequiresApproval: true,
        RemoteMemoryReadWrite: true,
        FileSendRequiresApproval: true,
        DisabledTools:
        [
            "ask_user", "subAgent_*", "team_*", "mcp_add_server",
            "media_generate", "create_skill", "install_skill", "skill_market", "register_artifact",
        ]);
}

/// <summary>审批请求（绑定内容 hash 与目标；设计 §9 ④）。</summary>
public sealed record ApprovalRequest(
    string Kind,                 // "send-file" | "run-command" | "disclosure"
    string Title,
    string DetailJson,           // 参数/内容 hash/目标等结构化明细
    ContentRef? File,            // 外发文件时必填（发送前重验 sha256，防审批后替换）
    DateTimeOffset ExpiresAt);

public enum ApprovalDecision
{
    Approved = 1,
    Denied = 2,
    Expired = 3,
    Cancelled = 4,
}

/// <summary>审批与状态出口（本地审批/IM 审批各自实现；InteractionSink）。</summary>
public interface IApprovalPort
{
    /// <summary>请求用户审批；超时返回 Expired，会话删除/撤权返回 Cancelled。</summary>
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 转写事件（设计 §11.5）：turn_events 的权威形态，提交前随 CommitPending 持久化，
/// 投影（transcript.jsonl）按 (TurnId, EventIndex) 确定性重放。
/// </summary>
public sealed record TurnEvent(string TurnId, int EventIndex, string Role, string Text, string? ArtifactsJson)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(TurnId) && EventIndex >= 0 && !string.IsNullOrWhiteSpace(Role);
}

/// <summary>非安全工具尝试记录（写前标记的内存对应物；设计 §11.4）。</summary>
public sealed record UnsafeToolAttempt(string ToolName, string DetailJson, DateTimeOffset AttemptedAt);

/// <summary>
/// 回合执行结果：执行器交回的事件批次与副作用记录。Completed=false 表示回合未完成
/// （取消/失败），提交协议按状态矩阵处置，不自动重跑非安全副作用。
/// </summary>
public sealed record TurnOutcome(
    bool Completed,
    string? AnswerText,
    IReadOnlyList<TurnEvent> Events,
    IReadOnlyList<UnsafeToolAttempt> UnsafeAttempts,
    string? Error,
    byte[]? SnapshotPayload = null,   // S7：真实执行器（MAF）携带的序列化快照；null=调用方按旧约定构造
    IReadOnlyList<TurnExecutionFile>? Files = null)   // S8 附件互传：产出文件（Blob ContentRef；随末分片 outbox 交付）
{
    public static TurnOutcome CompletedOk(string answer, IReadOnlyList<TurnEvent> events) =>
        new(true, answer, events, [], null);

    public static TurnOutcome Failed(string error, IReadOnlyList<TurnEvent> events, IReadOnlyList<UnsafeToolAttempt> unsafeAttempts) =>
        new(false, null, events, unsafeAttempts, error);
}

/// <summary>回合执行上下文（设计 §7 精简形态）：入口绑定不可变标识，执行中不读可变"当前会话"字段。</summary>
public sealed record TurnExecutionContext(
    string SessionId,
    long Generation,
    long ExpectedRevision,
    string TurnId,
    TurnOrigin Origin,
    PrincipalRef? Principal,       // IM 来源必填；本地来源为 null
    ReplyTarget? ReplyTarget,      // IM 来源必填；本地来源为 null → 不外发
    PermissionPolicy Permissions,
    IApprovalPort Approvals,
    string? UserText = null,   // S8：入站用户文本（调度器从 Inbox 行传入；真实执行器读取）
    IReadOnlyList<TurnExecutionAttachment>? Attachments = null,   // S8 附件：统一管道描述符（插件物化）
    Func<TurnExecutionAttachment, CancellationToken, Task<TurnExecutionAttachmentBlob?>>? MaterializeAttachment = null)   // 物化委托：宿主组装（通道插件下载→Blob→字节）
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(SessionId) &&
        !string.IsNullOrWhiteSpace(TurnId) &&
        (Origin == TurnOrigin.Local
            ? Principal is null && ReplyTarget is null
            : Principal is { IsValid: true } && ReplyTarget is { IsValid: true });

    /// <summary>
    /// 转写回写委托（additive init 属性，第四轮 N6 纪律；doc/Voice/01 §3.2.0 P2）：
    /// 由 TurnDispatcher 在构造 context 时闭包绑定（唯一同时持有 InboxRow 与 MessagingStore 处——
    /// ImComposition 拿不到 row.Id）。执行器物化后转写完成时调用；行不可写时执行器降级内存态。
    /// </summary>
    public Func<TurnExecutionAttachment, ImAttachmentTranscript, Task>? PersistTranscript { get; init; }
}

/// <summary>
/// 转写记录（doc/Voice/01 §3.2.0，第四轮 N8：落 Runtime.Abstractions——放 Messaging.Abstractions
/// 会程序集循环）：平台文本轨 Engine="platform"；宿主 ASR 回写填 IdempotencyKey/Engine/Duration。
/// 属性式 class（评审纪律：共享契约新类型禁位置参数）。
/// </summary>
public sealed class ImAttachmentTranscript
{
    /// <summary>转写文本（含标点）。</summary>
    public required string Text { get; init; }

    /// <summary>幂等键（RemoteId + blob SHA 前 16 hex）；平台文本轨无 key。</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>引擎来源："platform"=平台文本轨；其余=ASR 引擎名。</summary>
    public string? Engine { get; init; }

    /// <summary>音频时长（计费/遥测）。</summary>
    public TimeSpan? Duration { get; init; }
}

/// <summary>
/// 受控回合执行端口（§3.2）：S2～S6 由 FakeExecutor/脚本执行器实现；
/// S7 才对接真实 BrookAgent/MAF。基础组件禁止依赖 Avalonia/App/Controller。
/// </summary>
public interface ITurnExecutor
{
    Task<TurnOutcome> ExecuteAsync(TurnExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>会话快照：不透明载荷 + 版本元数据（S2 固定 JSON 字节；S7 换 MAF 适配，存储层无感）。</summary>
public sealed record SessionSnapshot(long Generation, long Revision, string CommitId, ReadOnlyMemory<byte> Payload)
{
    public bool IsValid => Generation > 0 && Revision >= 0 && !string.IsNullOrWhiteSpace(CommitId);
}

/// <summary>入站附件（统一管道 §8.1）：调度器从 Inbox attachments_json 解析后随上下文传递；处理由通道插件物化。</summary>
public sealed record TurnExecutionAttachment(
    string ChannelId,
    string InstanceId,
    string Provider,          // 物化器注册键（如 "telegram-file"/"bridge-blob"）
    string RemoteId,
    string? Name,
    string? MediaType,
    int Kind,                 // ImAttachmentKind：1=File 2=Image 3=Voice 4=Video
    long? DeclaredBytes)
{
    /// <summary>转写记录（平台文本轨或上回合 ASR 回写；执行器幂等注入依据，doc/Voice/01 §3.2.0 P1）。null=尚无转写。</summary>
    public ImAttachmentTranscript? Transcript { get; init; }

    /// <summary>物化状态数值（ImAttachmentState 语义：1=Referenced 2=Materialized 3=Expired 4=Rejected 5=BlockedByProvider；
    /// 用 int 避免本程序集依赖 Messaging.Abstractions——第四轮 N8/N9 纪律）。调度器从描述符透传。</summary>
    public int State { get; init; }
}

/// <summary>物化结果（通道插件下载 → 宿主 Blob → 字节回读；多模态模型输入用）。
/// LocalPath = 宿主 Blob 磁盘路径（会话界面用户侧渲染附件用；宿主按自身 Blob 约定填充）。</summary>
public sealed record TurnExecutionAttachmentBlob(
    string? BlobId,
    byte[] Bytes,
    string? MediaType,
    string? FileName,
    long Length,
    string? LocalPath = null);

/// <summary>回合产出文件（附件互传出站方向）：执行器写入宿主 Blob 后回 ContentRef → outbox files_json → 插件上传。</summary>
public sealed record TurnExecutionFile(string FileName, ContentRef Content);
