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

    /// <summary>
    /// 类别加载器（S8）：index/State 的 Category 命中已注册类别时，安装与恢复改由外部宿主加载
    /// （如 IM 通道插件经 PluginPackageLoader 进新宿主），不再走旧 IHelperPlugin 生命周期。
    /// 由 Helper 组合根注册；Agent.Core 不感知具体宿主。
    /// </summary>
    private readonly Dictionary<string, Func<string, Task>> _categoryLoaders = [];
    /// <summary>调用方对特定插件 id 的类别覆盖（服务器 index 缺 category 字段时的兜底）。</summary>
    private readonly Dictionary<string, string> _categoryOverrides = [];

    /// <summary>注册类别加载器（每类别一个；重复注册覆盖）。loader 接收安装目录。</summary>
    public void RegisterCategoryLoader(string category, Func<string, Task> loader) =>
        _categoryLoaders[category] = loader;

    /// <summary>声明指定插件 id 的类别覆盖（调用方已知它是 IM 插件——服务器 index 可能缺字段）。</summary>
    public void SetCategoryOverride(string pluginId, string category) =>
        _categoryOverrides[pluginId] = category;

    private bool TryGetCategoryLoader(string category, out Func<string, Task> loader)
    {
        return _categoryLoaders.TryGetValue(category ?? "", out loader!);
    }

    private readonly Dictionary<string, Func<string, Task>> _categoryUnloaders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>注册类别卸载器（外部宿主提供）：卸载前释放程序集（注销工厂/停实例/卸 ALC），
    /// 否则 Windows 上 DLL 被占用导致卸载失败（"Access to the path ... is denied"）。</summary>
    public void RegisterCategoryUnloader(string category, Func<string, Task> unloader) =>
        _categoryUnloaders[category] = unloader;

    /// <summary>由外部宿主加载的类别（不适用旧 IHelperPlugin 生命周期）。</summary>
    private static bool IsExternalCategory(string? category) =>
        string.Equals(category, "im", StringComparison.OrdinalIgnoreCase);

    /// <summary>获取条目的有效类别：优先调用方覆盖，回退 index 声明。</summary>
    internal string GetEffectiveCategory(PluginIndexEntry entry)
    {
        if (_categoryOverrides.TryGetValue(entry.Id, out var overrideCat))
            return overrideCat;
        return entry.Category;
    }

    /// <summary>安装（下载+校验+入位）并加载启用；progress 回报下载/校验/解压进度。</summary>
    public async Task InstallAsync(PluginIndexEntry entry, CancellationToken ct = default,
        IProgress<PluginInstallProgress>? progress = null)
    {
        var dir = await _catalog.InstallAsync(entry, ct, progress);
        var entryDll = FindEntryDll(dir);
        // S8：类别分流——IM 通道等类别由注册的外部宿主加载（旧生命周期不适用）
        var effectiveCategory = GetEffectiveCategory(entry);
        if (TryGetCategoryLoader(effectiveCategory, out var categoryLoader))
        {
            _state.SetInstalled(entry.Id, entry.Latest, enabled: true,
                Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)),
                string.IsNullOrEmpty(entry.Icon) ? null : entry.Icon,
                effectiveCategory);
            await categoryLoader(dir).ConfigureAwait(false);
            progress?.Report(new PluginInstallProgress(PluginInstallProgress.Done, 0, null));
            return;
        }
        if (IsExternalCategory(effectiveCategory))
        {
            // 外部宿主类别（如 im）但加载器尚未注册：绝不回落旧生命周期
            //（插件会因缺 IM 扩展契约抛 ContractVersionMissing，且能力不会被宿主注册）
            throw new InvalidOperationException(
                $"[ServiceNotGranted] IM 宿主尚未就绪，无法安装 {entry.Id}；请稍后重试（或重启 Helper）。");
        }
        progress?.Report(new PluginInstallProgress(PluginInstallProgress.Done, 0, null));
        await LoadFromDirectoryAsync(entry.Id, entry.Latest, dir, entryDll, enabled: true);
        // 实际入位目录名（热插拔下可能带 -r{n} 后缀），恢复加载时按此定位
        _state.SetInstalled(entry.Id, entry.Latest, enabled: true,
            Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)),
            string.IsNullOrEmpty(entry.Icon) ? null : entry.Icon);
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
        var category = _state.Snapshot().TryGetValue(id, out var st) ? st.Category : "plugin";
        Func<string, Task>? externalUnloader = null;
        var hasUnloader = IsExternalCategory(category)
            && _categoryUnloaders.TryGetValue(category, out externalUnloader);
        bool unloaded;
        if (hasUnloader && externalUnloader is not null)
        {
            // 外部宿主负责释放（停实例→注销工厂→卸 ALC→GC）。
            // 即便宿主释放过程中抛错（旧插件在取消路径抛异常），也继续走"标记删除 + 状态清理"，
            // 不让用户卡在"卸载不掉"的死局里。
            try
            {
                await externalUnloader(id).ConfigureAwait(false);
                unloaded = true;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[plugins] {id} 宿主释放异常（继续卸载）：{ex.Message}");
                unloaded = true;   // 已尽力释放：允许目录标记删除
            }
        }
        else
        {
            unloaded = await _registry.TryUnloadAsync(id);
        }
        var version = _state.GetVersion(id);
        if (version is not null)
        {
            var dir = ResolveDir(id, version);
            if (Directory.Exists(dir))
            {
                if (unloaded)
                {
                    try { Directory.Delete(dir, true); }
                    catch (IOException)
                    {
                        // 程序集仍被占用（ALC 回收滞后）：改名为待清理目录，下次启动清扫
                        var trash = dir + ".deleted-" + Guid.NewGuid().ToString("N")[..8];
                        try { Directory.Move(dir, trash); } catch { /* 留着下次再试 */ }
                        _log?.Invoke($"[plugins] {id} 目录占用中，已标记清理：{Path.GetFileName(trash)}");
                    }
                    catch (UnauthorizedAccessException)
                    {
                        var trash = dir + ".deleted-" + Guid.NewGuid().ToString("N")[..8];
                        try { Directory.Move(dir, trash); } catch { }
                        _log?.Invoke($"[plugins] {id} 目录占用中，已标记清理：{Path.GetFileName(trash)}");
                    }
                }
                else
                {
                    _log?.Invoke($"[plugins] {id} 未能卸载（宿主未释放），目录保留：{dir}");
                }
            }
        }
        _state.Remove(id);
        return unloaded;
    }

    // ── 装饰链 / 设置 / 生命周期 ─────────────────────────────────────────

    /// <summary>聚合已启用插件的聊天客户端装饰器；无 → null（BrookAgent 接 ModelRouter/主客户端）。</summary>
    public Func<IChatClient, IChatClient>? BuildChatClientPipeline() => _registry.BuildChatClientPipeline();

    /// <summary>插件图标 emoji（index/contract 声明，无 icon.png 时轨道芯片回退显示）。</summary>
    public string GetPluginIcon(string id) =>
        _state.Snapshot().TryGetValue(id, out var e) ? e.Icon : "";

    /// <summary>插件图标路径：优先注册目录根的 icon.png；缺失时回退扫描同 id 的兄弟目录
    /// （重装后缀 -rN 场景：旧目录无图标、新目录有），取最新修改的一份；都没有返回 null。</summary>
    public string? GetIconPath(string id)
    {
        var version = _state.GetVersion(id);
        if (version is null)
        {
            return null;
        }
        var parent = Path.Combine(Path.Combine(_workspaceRoot, "plugins"), id);
        var registered = Path.Combine(parent, _state.Snapshot()[id].Dir ?? version);
        var registeredPath = Path.Combine(registered, "icon.png");
        if (File.Exists(registeredPath))
        {
            return registeredPath;
        }
        string? best = null;
        var bestUtc = DateTime.MinValue;
        try
        {
            if (Directory.Exists(parent))
            {
                foreach (var dir in Directory.EnumerateDirectories(parent))
                {
                    var cand = Path.Combine(dir, "icon.png");
                    if (!File.Exists(cand))
                    {
                        continue;
                    }
                    var utc = File.GetLastWriteTimeUtc(cand);
                    if (utc > bestUtc)
                    {
                        bestUtc = utc;
                        best = cand;
                    }
                }
            }
        }
        catch
        {
            // 枚举失败按无图标处理
        }
        return best;
    }

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
                // S8：类别分流恢复（im 类别由外部宿主加载）
                if (TryGetCategoryLoader(entry.Category, out var categoryLoader))
                {
                    await categoryLoader(dir).ConfigureAwait(false);
                    _log?.Invoke($"[plugins] {id} {entry.Version} 已由类别宿主恢复加载（{entry.Category}）");
                    continue;
                }
                if (IsExternalCategory(entry.Category))
                {
                    // 外部宿主类别但加载器未注册（Agent 早于 IM 组合启动）：跳过旧生命周期，
                    // IM 组合启动时会自行扫描 plugins/{id}/ 加载（通道与登录能力）
                    _log?.Invoke($"[plugins] {id} 属外部宿主类别（{entry.Category}），交由 IM 宿主加载");
                    continue;
                }
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
