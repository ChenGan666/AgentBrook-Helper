namespace AgentBrook.A2A.Core.Identity;

public sealed class NodeIdentity
{
    public required string NodeId { get; init; }
    public required string DisplayName { get; set; }

    /// <summary>
    /// 展示用图标（emoji 或图标键），随配对、Profile 和会话事件对外发送，
    /// 供对端 Helper 在对话中显示与识别本节点。
    /// </summary>
    public required string Icon { get; set; }
    public required string PublicKeyBase64 { get; init; }
    public required string InstanceId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
