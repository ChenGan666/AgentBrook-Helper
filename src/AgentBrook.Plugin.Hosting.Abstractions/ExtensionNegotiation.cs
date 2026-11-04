using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Plugin.Hosting.Abstractions;

/// <summary>
/// 插件对扩展上下文的请求。RequiredServices 声明所需宿主服务（§15.2 服务清单）；
/// 未授予的服务在上下文中取不到（GetService 返回 null），宿主也可在协商阶段直接拒绝。
/// </summary>
public sealed record ExtensionRequest(
    string PluginId,
    string MinContractVersion,
    IReadOnlyList<string> RequiredServices)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(PluginId) &&
        !string.IsNullOrWhiteSpace(MinContractVersion);
}

/// <summary>
/// 新宿主扩展协商入口：旧 IHelperPlugin 实现的插件在 InitializeAsync 中
/// 以 <c>host as IPluginHostExtension</c> 查询；宿主不支持（null）或版本不匹配时，
/// 插件必须<b>明确拒绝初始化</b>（抛 InvalidOperationException，ReasonCode=
/// ContractVersionMissing / ContractVersionMismatch），不得半启动、不得偷偷降级（C02）。
/// </summary>
public interface IPluginHostExtension
{
    Task<IExtensionContext?> NegotiateAsync(ExtensionRequest request, CancellationToken cancellationToken);
}

/// <summary>协商成功的扩展上下文：绑定五级身份与活动租约；按权限提供服务访问器（拿不到即 null）。</summary>
public interface IExtensionContext : IAsyncDisposable
{
    string ContractVersion { get; }
    InstanceRef Instance { get; }
    ActivationId Activation { get; }

    /// <summary>
    /// 按权限获取宿主服务（§15.2）。返回 null = 未授予（默认拒绝）；
    /// 活动租约失效后所有服务调用即失效（H02）。
    /// </summary>
    T? GetService<T>() where T : class;

    /// <summary>宿主结构化日志出口（凭据自动隐藏）。</summary>
    void Log(string message);
}
