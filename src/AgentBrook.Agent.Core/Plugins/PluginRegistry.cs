using System.Reflection;
using AgentBrook.Plugin.Abstractions;
using Microsoft.Extensions.AI;

namespace AgentBrook.Agent.Plugins;

/// <summary>
/// 已加载插件注册表（进程内）：实例 + 能力 + 隔离上下文；聚合已启用插件的能力
/// （BuildChatClientPipeline = 聊天客户端装饰链）。停用 = 能力注销 + ALC 卸载
/// （回收失败标记“重启后生效”——诚实体感）。
/// </summary>
public sealed class PluginRegistry
{
    private sealed record Loaded(
        IHelperPlugin Plugin,
        PluginLoadContext Context,
        Assembly EntryAssembly,
        IChatClientDecorator? Decorator,
        ISettingsContributor? Settings,
        bool Enabled,
        WeakReference AlcWeakRef);

    private readonly Dictionary<string, Loaded> _loaded = new(StringComparer.Ordinal);
    private int _version;

    public int LoadedCount => _loaded.Count;

    /// <summary>管线版本号：任何组合变化（安装/启停/卸载）+1，宿主管线宿主据此缓存/换链（热插拔）。</summary>
    public int Version => _version;

    /// <summary>版本变化通知：管线宿主立即弃缓存链（否则卸载时的 GC 回收循环收不到仍被缓存引用的旧装饰器）。</summary>
    public event Action? VersionChanged;

    /// <summary>插件侧触发的管线失效（设置开关影响装饰形态，如脱敏总开关）。</summary>
    public void BumpVersion()
    {
        Interlocked.Increment(ref _version);
        VersionChanged?.Invoke();
    }

    /// <summary>注册已加载插件（ALC 加载与入口实例化由调用方完成后传入）。</summary>
    public void Add(IHelperPlugin plugin, PluginLoadContext context, Assembly entryAssembly, bool enabled)
    {
        _loaded[plugin.Id] = new Loaded(
            plugin,
            context,
            entryAssembly,
            plugin as IChatClientDecorator,
            plugin as ISettingsContributor,
            enabled,
            new WeakReference(context));
        BumpVersion();
    }

    public bool IsLoaded(string id) => _loaded.ContainsKey(id);

    public ISettingsContributor? GetSettingsContributor(string id) =>
        _loaded.TryGetValue(id, out var loaded) ? loaded.Settings : null;

    /// <summary>按可选能力接口在已启用插件中查找实现（T5.3 回合遥测等）。</summary>
    public T? GetCapability<T>() where T : class, IPluginCapability =>
        _loaded.Values.FirstOrDefault(l => l.Enabled && l.Plugin is T)?.Plugin as T;

    /// <summary>聚合所有已启用插件的聊天客户端装饰器（按注册序依次包装）；无装饰器 → null。</summary>
    public Func<IChatClient, IChatClient>? BuildChatClientPipeline()
    {
        var decorators = _loaded.Values
            .Where(l => l.Enabled && l.Decorator is not null)
            .Select(l => l.Decorator!)
            .ToList();
        // 反向折叠：先注册的装饰器位于最外层（请求先经过，语义同 ASP.NET 中间件注册序）
        var ordered = decorators.AsEnumerable().Reverse().ToList();
        if (ordered.Count == 0)
        {
            return null;
        }
        return inner =>
        {
            foreach (var decorator in ordered)
            {
                // 插件返回 null = 其当前不装饰（如总开关关闭）→ 原样透传
                inner = decorator.Decorate(inner) ?? inner;
            }
            return inner;
        };
    }

    /// <summary>启停标记（管线宿主检测版本号变化后自动换链，热生效）。</summary>
    public void SetEnabled(string id, bool enabled)
    {
        if (_loaded.TryGetValue(id, out var loaded) && loaded.Enabled != enabled)
        {
            _loaded[id] = loaded with { Enabled = enabled };
            BumpVersion();
        }
    }

    /// <summary>
    /// 停用并卸载：Shutdown → 注销 → ALC Unload → 弱引用检查（真异步，不阻塞调用线程）。
    /// 回收失败（旧链实例尚被在途请求引用等）返回 false——文件待 GC/重启后可删，
    /// 聊天能力已即时注销（管线宿主换链）。
    /// </summary>
    public async Task<bool> TryUnloadAsync(string id)
    {
        if (!_loaded.TryGetValue(id, out var loaded))
        {
            return true;
        }
        _loaded.Remove(id);
        BumpVersion();
        await loaded.Plugin.ShutdownAsync();
        loaded.Context.Unload();
        var weak = loaded.AlcWeakRef;
        for (var i = 0; i < 10 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (!weak.IsAlive)
            {
                break;
            }
            await Task.Delay(50);   // 让出线程：给终结/回收一点真实时间窗
        }
        return !weak.IsAlive;
    }
}
