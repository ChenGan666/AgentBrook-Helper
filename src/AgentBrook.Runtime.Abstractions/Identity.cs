namespace AgentBrook.Runtime.Abstractions;

/// <summary>
/// 五级身份（设计 §15.2）：PluginId（软件包）→ CapabilityId（能力类型）→ InstanceId（用户创建的配置实例）
/// → AccountId（平台账号）→ PrincipalId（被授权发信人）。
/// 所有持久化行绑定稳定标识（InstanceId + 相应 Epoch），不绑定短期活动租约。
/// </summary>
public sealed record PluginRef(string PluginId, string Version)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(PluginId) && !string.IsNullOrWhiteSpace(Version);
}

/// <summary>插件注册的能力类型描述（工厂级，实例创建前）。</summary>
public sealed record CapabilityRef(string PluginId, string CapabilityId)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(PluginId) && !string.IsNullOrWhiteSpace(CapabilityId);
}

/// <summary>用户创建的配置实例（稳定；持久化资源与任务归属）。</summary>
public sealed record InstanceRef(string PluginId, string CapabilityId, string InstanceId)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(PluginId) &&
        !string.IsNullOrWhiteSpace(CapabilityId) &&
        !string.IsNullOrWhiteSpace(InstanceId);

    public CapabilityRef Capability => new(PluginId, CapabilityId);
}

/// <summary>
/// 实例活动租约（设计 §15.4）：每次 Start/恢复/更新切换签发（短期）。
/// 宿主绑定的句柄带 ActivationId，每次 Sink/投递/网络/凭据调用都做存活校验，失效即拒。
/// </summary>
public readonly record struct ActivationId(string Value)
{
    public static ActivationId New() => new("a-" + Guid.NewGuid().ToString("N"));
    public bool IsValid => !string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
}

/// <summary>实例绑定端点身份（设计 §4.1）：宿主为每个通道实例创建的接收句柄随句柄只读携带。</summary>
public sealed record ImEndpointIdentity(string PluginId, string InstanceId, long AccountEpoch)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(PluginId) && !string.IsNullOrWhiteSpace(InstanceId);
}

/// <summary>平台账号身份（含账号代次；换号即 +1，旧账号任务错投防护）。</summary>
public sealed record AccountRef(string PluginId, string InstanceId, string AccountId, long AccountEpoch)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(PluginId) && !string.IsNullOrWhiteSpace(AccountId);
}

/// <summary>
/// 被授权主体（配对身份）。AuthorizationEpoch 撤权/变化即 +1，
/// 执行前、工具调用前、发送前重新验证，不符即取消并记录原因（设计 §11.6）。
/// </summary>
public sealed record PrincipalRef(string PrincipalId, long AuthorizationEpoch, DisclosureScope Scope)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(PrincipalId);
}

/// <summary>披露范围（P0 单一档：私聊、允许还原本人相关数据；设计 §9/§13）。</summary>
public enum DisclosureScope
{
    /// <summary>P0 唯一合法档位。新增档位属 P1 多信任域设计，须先落地按主体分区。</summary>
    PairedPrivate = 1,
}

/// <summary>
/// 会话身份双版本（设计 §6.1）：Generation 删除重建 +1（删除防护）；
/// Revision 每次成功提交 +1（陈旧快照检测：先后执行的执行体必须基于最新 Revision）。
/// </summary>
public readonly record struct SessionVersion(long Generation, long Revision)
{
    public static readonly SessionVersion Initial = new(1, 0);
    public SessionVersion NextRevision() => new(Generation, Revision + 1);
    public override string ToString() => $"g{Generation}/r{Revision}";
}
