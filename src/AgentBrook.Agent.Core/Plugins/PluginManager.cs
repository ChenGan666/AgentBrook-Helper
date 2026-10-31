using System.Reflection;
using System.Runtime.InteropServices;
using AgentBrook.Agent.Infrastructure;
using AgentBrook.Plugin.Abstractions;
using Microsoft.Extensions.AI;

namespace AgentBrook.Agent.Plugins;

/// <summary>
/// 插件系统门面（doc/18 §4）：浏览（服务端索引）/ 安装（下载+校验+入位+加载）/ 启停 / 卸载
/// / 聊天客户端装饰链聚合。状态持久化 workspace/plugins/state.json；插件目录
/// workspace/plugins/{id}/{version}/。
/// </summary>
public sealed class PluginManager
{
    private readonly string _workspaceRoot;
    private readonly string _indexUrl;
    private readonly Action<string>? _log;
    private readonly PluginStateStore _state;
    private readonly PluginCatalog _catalog;
    private readonly PluginRegistry _registry = new();
    private readonly string _language;

    public PluginManager(string workspaceRoot, string indexUrl,
        string language = "zh", Action<string>? log = null)
    {
        _workspaceRoot = workspaceRoot;
        _indexUrl = indexUrl;
        _language = language;
        _log = log;
        _state = new PluginStateStore(Path.Combine(workspaceRoot, "plugins", "state.json"));
        _catalog = new PluginCatalog(log)
        {
            IndexBase = indexUrl,
            PluginsRoot = Path.Combine(workspaceRoot, "plugins"),
        };
    }

    public PluginRegistry Registry => _registry;

    public string ModelsUrl => new Uri(new Uri(_indexUrl), "models/models.json").ToString();

    // ── 浏览 ─────────────────────────────────────────────────────────────

    /// <summary>拉取服务端可用插件列表。</summary>
    public async Task<IReadOnlyList<PluginIndexEntry>> FetchAvailableAsync(CancellationToken ct = default) =>
        (await _catalog.FetchAsync(new Uri(_indexUrl), ct)).Plugins;

    /// <summary>本地已装清单（快照）。</summary>
    public IReadOnlyDictionary<string, PluginStateEntry> ListInstalled() => _state.Snapshot();

    public bool IsEnabled(string id) => _state.IsEnabled(id) && _registry.IsLoaded(id);

    // ── 安装 / 卸载 / 启停 ───────────────────────────────────────────────

    /// <summary>安装（下载+校验+入位）并加载启用；progress 回报下载/校验/解压进度。</summary>
    public async Task InstallAsync(PluginIndexEntry entry, CancellationToken ct = default,
        IProgress<PluginInstallProgress>? progress = null)
    {
        var dir = await _catalog.InstallAsync(entry, ct, progress);
        progress?.Report(new PluginInstallProgress(PluginInstallProgress.Done, 0, null));
        var entryDll = FindEntryDll(dir);
        await LoadFromDirectoryAsync(entry.Id, entry.Latest, dir, entryDll, enabled: true);
        // 实际入位目录名（热插拔下可能带 -r{n} 后缀），恢复加载时按此定位
        _state.SetInstalled(entry.Id, entry.Latest, enabled: true, Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)));
    }

    /// <summary>解析插件安装目录：优先 state.json 记录的实际目录名（热插拔下可能带 -r{n}），缺省 {version}。</summary>
    private string ResolveDir(string id, string version)
    {
        var leaf = _state.GetDirName(id) ?? version;
        return Path.Combine(Path.Combine(_workspaceRoot, "plugins"), id, leaf);
    }

    /// <summary>启用（已装未加载 → 异步加载；已加载 → 标记启用）。真异步：插件 Initialize 不阻塞调用线程。</summary>
    public async Task SetEnabledAsync(string id, bool enabled)
    {
        _state.SetEnabled(id, enabled);
        if (enabled)
        {
            if (!_registry.IsLoaded(id))
            {
                var version = _state.GetVersion(id);
                if (version is null)
                {
                    throw new InvalidOperationException($"插件 {id} 未安装");
                }
                var dir = ResolveDir(id, version);
                var entryDll = FindEntryDll(dir);
                await LoadFromDirectoryAsync(id, version, dir, entryDll, enabled: true);
            }
            _registry.SetEnabled(id, true);
        }
        else
        {
            _registry.SetEnabled(id, false);
        }
    }

    /// <summary>卸载：停用 + 异步卸载 ALC + 删除目录 + 清状态。回收失败如实提示（目录由下次安装清扫）。</summary>
    public async Task<bool> UninstallAsync(string id)
    {
        _state.SetEnabled(id, false);
        var unloaded = await _registry.TryUnloadAsync(id);
        var version = _state.GetVersion(id);
        if (version is not null)
        {
            var dir = ResolveDir(id, version);
            if (Directory.Exists(dir) && unloaded)
            {
                Directory.Delete(dir, true);
            }
        }
        _state.Remove(id);
        return unloaded;
    }

    // ── 装饰链 / 设置 / 生命周期 ─────────────────────────────────────────

    /// <summary>聚合已启用插件的聊天客户端装饰器；无 → null（BrookAgent 接 ModelRouter/主客户端）。</summary>
    public Func<IChatClient, IChatClient>? BuildChatClientPipeline() => _registry.BuildChatClientPipeline();

    /// <summary>显示层一次性还原（v1.0.9 修订）：回合结束后宿主对最终文本调用；无还原插件时原样返回。</summary>
    public string RestoreDisplayText(string text)
    {
        var restorer = _registry.GetCapability<IDisplayTextRestorer>();
        return restorer?.RestoreDisplayText(text) ?? text;
    }

    /// <summary>插件设置贡献者（设置页渲染/写回）。</summary>
    public ISettingsContributor? GetSettingsContributor(string id) => _registry.GetSettingsContributor(id);

    /// <summary>启动恢复：按 state.json 加载全部 enabled 插件。</summary>
    public async Task RestoreAsync()
    {
        foreach (var (id, entry) in _state.Snapshot())
        {
            if (!entry.Enabled)
            {
                continue;
            }
            try
            {
                var dir = ResolveDir(id, entry.Version);
                var entryDll = FindEntryDll(dir);
                await LoadFromDirectoryAsync(id, entry.Version, dir, entryDll, enabled: true);
                _log?.Invoke($"[plugins] {id} {entry.Version} 已恢复加载");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[plugins] {id} 恢复失败：{ex.Message}");
            }
        }
        await Task.CompletedTask;
    }

    // ── 加载（ALC + 入口发现 + 宿主上下文注入） ──────────────────────────

    private string FindEntryDll(string pluginDir)
    {
        var manifest = Path.Combine(pluginDir, "manifest.json");
        if (File.Exists(manifest))
        {
            var m = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifest));
            var entry = m?.Entry;
            if (!string.IsNullOrEmpty(entry) && File.Exists(Path.Combine(pluginDir, entry)))
            {
                return Path.Combine(pluginDir, entry);
            }
        }
        // 无 manifest：取目录内唯一的非 Abstractions 插件 dll（约定名 AgentBrook.Plugin.*.dll）
        var candidates = Directory.GetFiles(pluginDir, "AgentBrook.Plugin.*.dll")
            .Where(p => !Path.GetFileName(p).Contains("Abstractions", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return candidates.Count == 1
            ? candidates[0]
            : throw new FileNotFoundException($"插件入口 dll 未找到：{pluginDir}");
    }

    private async Task LoadFromDirectoryAsync(string id, string version, string dir, string entryDll, bool enabled)
    {
        var context = new PluginLoadContext(entryDll);
        var assembly = context.LoadFromPlugin(entryDll);

        var entryType = assembly.GetTypes()
            .FirstOrDefault(t => t.IsClass
                && t.GetCustomAttribute<PluginEntryAttribute>() is { } attr
                && string.Equals(attr.Id, id, StringComparison.Ordinal)
                && typeof(IHelperPlugin).IsAssignableFrom(t))
            ?? throw new InvalidOperationException(
                $"插件 {id} 缺少标注 [PluginEntry(\"{id}\")] 且实现 IHelperPlugin 的类型");

        var plugin = (IHelperPlugin)Activator.CreateInstance(entryType)!;
        await plugin.InitializeAsync(new HostContext(_workspaceRoot, _language, _state, id, _log ?? (_ => { }),
            () => _registry.BumpVersion()));

        _registry.Add(plugin, context, assembly, enabled);
    }

    private sealed class HostContext(string workspaceRoot, string language,
        PluginStateStore state, string pluginId, Action<string> log,
        Action pipelineChanged) : IPluginHostContext
    {
        public string WorkspaceRoot { get; } = workspaceRoot;

        public string Language { get; } = language;

        public IPluginSettingsStore Settings { get; } = new ScopedSettingsStore(state, pluginId);

        public IPluginDownloadService Download => ResourceDownloader.Instance;

        public void Log(string message) => log($"[{pluginId}] {message}");

        public void RequestPipelineRebuild() => pipelineChanged();

        private sealed class ScopedSettingsStore(PluginStateStore state, string id) : IPluginSettingsStore
        {
            public string GetSettingsJson() => state.GetSettingsJson(id) ?? "{}";

            public void SetSettingsJson(string json) => state.SetSettingsJson(id, json);
        }
    }
}
