using AgentBrook.Messaging.Abstractions;
using AgentBrook.Plugin.Abstractions;
using AgentBrook.Plugin.Hosting.Abstractions;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Plugin.Im.Weixin;

/// <summary>
/// 微信通道插件入口（iLink / ClawBot，2026 腾讯开放的个人微信 Bot 通道）。
/// 实例创建经 <see cref="WeixinChannelFactory"/>（宿主注册后按用户配置驱动）。
/// </summary>
[PluginEntry("im-weixin")]
public sealed class WeixinPlugin : IHelperPlugin
{
    public string Id => "im-weixin";
    public string DisplayName => "WeChat (iLink)";
    public string Version => "1.0.0";

    internal IExtensionContext? Context { get; private set; }

    public async Task InitializeAsync(IPluginHostContext host, CancellationToken cancellationToken = default)
    {
        var extension = host as IPluginHostExtension
            ?? throw new InvalidOperationException(
                $"[{ImErrorCodes.ContractVersionMissing}] 宿主缺少 IM 扩展契约，插件拒绝以降级模式启动。");
        Context = await extension.NegotiateAsync(
            new ExtensionRequest(Id, HostingContract.Version, ["state-store", "credentials", "network"]),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"[{ImErrorCodes.ContractVersionMismatch}] 宿主契约版本不满足 {HostingContract.Version}，初始化拒绝。");
    }

    public Task ShutdownAsync()
    {
        Context = null;
        return Task.CompletedTask;
    }
}

/// <summary>通道能力工厂：Plugin → CapabilityFactory → EndpointInstance。</summary>
public sealed class WeixinChannelFactory : ICapabilityFactory
{
    public string PluginId => "im-weixin";
    public string CapabilityId => "weixin-channel";
    public string MinContractVersion => HostingContract.Version;
    public IReadOnlyList<string> RequiredServices { get; } = ["state-store", "credentials", "network"];

    /// <summary>clock：测试注入可控时钟；生产为系统时钟。</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public Task<ICapabilityInstance> CreateAsync(InstanceCreationContext context, CancellationToken cancellationToken)
    {
        var instance = new WeixinChannel(context.Instance, context.Activation, context.Services, "{}", Clock);
        return Task.FromResult<ICapabilityInstance>(instance);
    }
}

/// <summary>
/// 登录能力工厂（宿主驱动扫码登录用）：与通道共用实例命名空间 im-weixin+inst-1，
/// 因此登录写入的 bot-token 凭据可直接被通道使用。
/// </summary>
public sealed class WeixinLoginFactory : ICapabilityFactory
{
    public string PluginId => "im-weixin";
    public string CapabilityId => "weixin-login";
    public string MinContractVersion => HostingContract.Version;
    public IReadOnlyList<string> RequiredServices { get; } = ["credentials", "network"];

    public Task<ICapabilityInstance> CreateAsync(InstanceCreationContext context, CancellationToken cancellationToken)
    {
        var provider = new WeixinLoginProvider(context.Instance, context.Activation, context.Services);
        return Task.FromResult<ICapabilityInstance>(provider);
    }
}
