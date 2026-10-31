using Microsoft.Extensions.AI;

namespace AgentBrook.Agent.Plugins;

/// <summary>
/// 插件管线宿主（热插拔核心，doc/18 §4.3）：常驻聊天客户端栈中的一层，
/// 按注册表版本号缓存"已启用插件装饰链"的组合结果——安装/启停/卸载/设置开关即时生效，无需重启。
/// 链为 null（无启用插件）→ 直通内层，稳态零分配（仅一次版本号比较）。
/// 旧链实例换链后直接弃引用（不 Dispose：DelegatingChatClient 会连内层真客户端一起 Dispose），
/// 由 GC 回收；ALC 卸载在引用消亡后自然完成。
/// </summary>
public sealed class PluginPipelineChatClient : IChatClient
{
    private volatile IChatClient? _chain;
    private int _chainVersion = -1;
    private readonly object _buildGate = new();

    public PluginPipelineChatClient(IChatClient inner, PluginRegistry registry) : this(inner, registry, subscribe: true) { }

    private PluginPipelineChatClient(IChatClient inner, PluginRegistry registry, bool subscribe)
    {
        Inner = inner;
        Registry = registry;
        if (subscribe)
        {
            // 版本变化立即弃缓存链：让卸载流程里的 GC 回收循环能收走旧装饰器（否则被本类字段引用着收不掉）
            registry.VersionChanged += DropChain;
        }
    }

    private IChatClient Inner { get; }
    private PluginRegistry Registry { get; }

    private void DropChain()
    {
        _chain = null;
        _chainVersion = -1;
    }

    /// <summary>取当前生效链：版本号变化才重建（双检锁；组合本身轻量，竞争极小）。</summary>
    private IChatClient Resolve()
    {
        var version = Registry.Version;
        if (_chain is not null && _chainVersion == version)
        {
            return _chain;
        }
        lock (_buildGate)
        {
            if (_chain is null || _chainVersion != version)
            {
                // 插件 Decorate 返回 null = 其当前不装饰（如总开关关闭）→ 原样透传
                var pipeline = Registry.BuildChatClientPipeline();
                _chain = pipeline?.Invoke(Inner) ?? Inner;
                _chainVersion = version;
            }
            return _chain;
        }
    }

    /// <summary>在途请求持旧链引用直至完成（换链不中断进行中的回合）。</summary>
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Resolve().GetResponseAsync(messages, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Resolve().GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(PluginPipelineChatClient) ? this : Inner.GetService(serviceType, serviceKey);

    // 只退订事件：inner 归 AgentFactory 管辖；弃用的旧插件链由 GC 回收（见类注释）
    public void Dispose() => Registry.VersionChanged -= DropChain;
}
