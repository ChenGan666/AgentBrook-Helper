using AgentBrook.Runtime.Abstractions;
namespace AgentBrook.Messaging.Abstractions;

/// <summary>通道运行状态（设置页展示）。</summary>
public enum ImChannelRunState
{
    Starting = 1,
    Running = 2,
    Degraded = 3,
    Stopped = 4,
    Failed = 5,
}

/// <summary>通道状态变更事件载荷（含最近错误原因）。</summary>
public sealed record ImChannelStatus(ImChannelRunState State, string? Reason, DateTimeOffset At);

/// <summary>通道能力表（设计 §4.4）：管道分片/限额/格式化全部由此驱动。</summary>
public sealed record ImChannelCapabilities(
    int MaxTextChars,
    long MaxOutFileBytes,
    long MaxInFileBytes,
    int MaxFilesPerMessage,
    bool SupportsCaption,
    int CaptionMaxChars,
    string MarkdownMode,
    TimeSpan? ReplyWindow,
    int GroupRatePerMinute,
    bool HasOutboundIdempotencyKey)   // 桥=true（Unknown 可安全自动重试）；Telegram=false（Unknown 不自动重发）
{
    public bool IsValid => MaxTextChars > 0 && MaxOutFileBytes > 0 && MaxInFileBytes > 0 && MaxFilesPerMessage > 0;
}

/// <summary>启动上下文：注入实例绑定 Sink（可靠接收唯一入口）与通道配置。</summary>
public sealed record ImChannelStartContext(IImInboundSink Sink, string ConfigJson);

/// <summary>
/// 通道契约（设计 §4.1）：接收决策权在核心，连接管理在通道。
/// StartAsync 返回即"已具备接收能力"；StopAsync 返回即停止完成（在途协议调用排空/取消后）。
/// </summary>
public interface IImChannel : IAsyncDisposable
{
    string ChannelId { get; }
    string DisplayName { get; }
    bool IsRunning { get; }
    ImChannelCapabilities Capabilities { get; }

    Task StartAsync(ImChannelStartContext context, CancellationToken cancellationToken);

    Task StopAsync(TimeSpan gracePeriod);

    event Action<ImChannelStatus>? StatusChanged;

    /// <summary>统一出站投递（§4.2）；文本/文件便捷方法底层走同一协议。</summary>
    Task<DeliveryResult> SubmitDeliveryAsync(DeliveryRequest request, CancellationToken cancellationToken);

    /// <summary>附件物化（§8.1 T07，统一管道）：Provider 描述符 → 宿主 Blob（ContentRef）。
    /// 由各插件实现下载/格式处理；不支持附件的通道返回 null。</summary>
    Task<ContentRef?> MaterializeAsync(ImAttachmentDescriptor descriptor, CancellationToken cancellationToken);
}
