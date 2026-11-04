using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Plugin.Hosting.Abstractions;

/// <summary>
/// 能力工厂注册模型（设计 §15.2）：Plugin → CapabilityFactory → EndpointInstance(s)。
/// 注册返回可撤销租约（插件级）；实例创建由宿主按用户配置驱动（实例级租约另发）。
/// </summary>
public interface ICapabilityFactory
{
    /// <summary>所属插件包 id（五级身份之根；S3 补充——实例命名空间与审计归属需要）。</summary>
    string PluginId { get; }

    /// <summary>能力类型 id（同一插件可注册多种能力：通道/卡片渲染器/平台工具……各自独立授权）。</summary>
    string CapabilityId { get; }

    /// <summary>所需最低契约版本（协商失败加载前拒绝）。</summary>
    string MinContractVersion { get; }

    /// <summary>声明需要的宿主服务（授予层：请求 ∩ 宿主支持 ∩ 用户授权 ∩ 策略）。</summary>
    IReadOnlyList<string> RequiredServices { get; }

    /// <summary>创建实例。宿主传入实例标识与已授予服务；工厂不得绕过授予自行取服务。</summary>
    Task<ICapabilityInstance> CreateAsync(InstanceCreationContext context, CancellationToken cancellationToken);
}

/// <summary>实例创建上下文：宿主签发，随实例生命周期绑定活动租约。</summary>
public sealed record InstanceCreationContext(
    InstanceRef Instance,
    ActivationId Activation,
    IExtensionContext Services)
{
    public bool IsValid => Instance.IsValid && Activation.IsValid && Services is not null;
}

/// <summary>能力实例：宿主管理的生命周期单元（Start/Stop 由具体能力契约细化，如 IImChannel）。</summary>
public interface ICapabilityInstance : IAsyncDisposable
{
    InstanceRef Instance { get; }
    ActivationId Activation { get; }
}
