using System.Text.Json;
using AgentBrook.Agent.Infrastructure;
using AgentBrook.Agent.IM;
using AgentBrook.Helper.Core.Im;
using Microsoft.Agents.AI;
using System.Net.Http;
using AgentBrook.Helper.Support;
using AgentBrook.Messaging;
using AgentBrook.Messaging.Abstractions;
using AgentBrook.Plugin.Hosting;
using AgentBrook.Plugin.Hosting.Abstractions;
using AgentBrook.Runtime;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Helper.Core.Im;

/// <summary>IM 回合 UI 事件种类（TurnUiHook 载荷；气泡状态全局可见，文本增量仅选中会话透传）。</summary>
public enum ImTurnUiKind
{
    Started,
    Delta,
    Completed,
    Failed,
}

/// <summary>组合结果（U01/U08 断言面；UI 只读此结果，不触 DB/文件）。</summary>
public sealed record ImCompositionResult(
    bool Enabled,
    string StateJson,
    ImGateway? Gateway,
    MessagingService? Messaging,
    List<(string ChannelId, string InstanceId)> BoundChannels);

/// <summary>
/// IM 组合根（S8 任务1）：唯一受 <c>workspace/im/config.json → ImEnabled</c> 开关控制的组合入口。
/// <b>默认关闭 = 零副作用</b>：不创建 im/ 目录、不注册网关路由、不启动通道/恢复、不迁移会话（U01）。
/// 启用 = 网关路由挂 47852（不新增监听器）+ 版本化持久化附加到 BrookAgent + 启动恢复 + 通道插件包加载组合。
/// 通道实现经 PluginPackageLoader 从包目录加载（主应用不静态引用插件实现，§11.3/S9）。
/// </summary>
public sealed class ImComposition
{
    private readonly string _workspaceRoot;
    private readonly Func<AgentBrook.Agent.BrookAgent?> _agentAccessor;
    private readonly ImGateway _gateway;
    private readonly string _pluginsRoot;
    private ImSettings _settings;
    private VersionedSessionStore? _store;
    private MessagingService? _messaging;
    private PluginHost? _pluginHost;
    private readonly List<(string ChannelId, string InstanceId, string Fingerprint)> _bound = [];
    private readonly object _boundGate = new();
    private ImDatabase? _db;
    private HelperImAgentHost? _agentHost;
    private bool _started;
    private readonly List<IAsyncDisposable> _disposables = [];

    /// <summary>Telegram getMe 验证（设置页"测试连接"；真实平台调用，仅用户点击时发起）。</summary>
    public static async Task<(bool Ok, string Detail)> VerifyTelegramTokenAsync(string token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = await http.GetStringAsync($"https://api.telegram.org/bot{token}/getMe", CancellationToken.None)
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            if (!ok) return (false, "Telegram 返回 ok=false（token 无效？）");
            var result = doc.RootElement.GetProperty("result");
            var username = result.TryGetProperty("username", out var u) ? u.GetString() : null;
            return (true, $"@{username}");
        }
        catch (Exception ex)
        {
            return (false, $"连接失败：{ex.Message}");
        }
    }

    /// <summary>通道配置指纹（凭据引用 + baseUrl + 账号）：重新扫码后账号/节点变化必须重绑。</summary>
    private static string ConfigFingerprint(ImChannelConfig ch) =>
        $"{ch.SecretRef}|{ch.BaseUrl}|{ch.AccountId}";

    /// <summary>解绑单通道：停实例 → 解除 Sink/通道/身份 → 清绑定记录（重绑/卸载用）。</summary>
    private async Task UnbindChannelAsync(ImChannelConfig ch)
    {
        try
        {
            if (_pluginHost is not null)
            {
                var cap = ch.Type switch
                {
                    "telegram" => "telegram-channel",
                    "bridge" => "bridge-channel",
                    "weixin" => "weixin-channel",
                    _ => "",
                };
                // 必须带能力 id：三个通道实例 id 都是 inst-1，只按 instanceId 会停错实例
                await _pluginHost.StopInstanceAsync(cap, ch.InstanceId, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            ImDiag.Log($"[im] 解绑停实例失败 {ch.ChannelId}：{ex.Message}");
        }
        try { _messaging?.UnbindChannel(ch.ChannelId, ch.InstanceId); }
        catch (Exception ex) { ImDiag.Log($"[im] 解绑失败 {ch.ChannelId}：{ex.Message}"); }
        lock (_boundGate) _bound.RemoveAll(b => b.ChannelId == ch.ChannelId && b.InstanceId == ch.InstanceId);
        ImDiag.Log($"[im] 通道已解绑：{ch.ChannelId}");
    }

    /// <summary>
    /// 强制重新绑定配置中的所有启用通道（诊断/恢复入口：不重启即可重试）。
    /// 返回每个通道的结果行（类型、是否绑定成功、失败原因）。
    /// </summary>
    public static async Task<List<string>> RebindConfiguredChannelsAsync()
    {
        var results = new List<string>();
        await EnsureStartedAsync().ConfigureAwait(false);
        var composition = Instance;
        if (composition is null)
        {
            results.Add("IM 组合未启动（检查是否已启用 / 日志 [im]）");
            return results;
        }
        composition._settings = ImSettings.Load(composition._workspaceRoot);
        var channels = composition._settings.Channels.Where(c => c.Enabled).ToList();
        if (channels.Count == 0) results.Add("配置中没有启用的通道");
        foreach (var ch in channels)
        {
            try
            {
                await composition.UnbindChannelAsync(ch).ConfigureAwait(false);   // 先解绑（幂等，不累积实例）
                await composition.BindChannelAsync(ch).ConfigureAwait(false);
                results.Add($"{ch.ChannelId}：绑定成功");
            }
            catch (Exception ex)
            {
                results.Add($"{ch.ChannelId}：失败（{ex.Message}）");
            }
        }
        return results;
    }

    /// <summary>
    /// 确保某通道已绑定（配置保存后调用）：组合未启动则先启动；**已启动则只补绑定新通道**
    /// （EnsureStartedAsync 命中缓存会直接返回——这正是"登录成功但发消息无反应"的成因）。
    /// 返回是否已绑定。
    /// </summary>
    public static async Task<bool> EnsureChannelBoundAsync(string channelType, CancellationToken ct = default)
    {
        await EnsureStartedAsync().ConfigureAwait(false);
        var composition = Instance;
        if (composition is null) return false;

        // 重新读盘：配置可能刚被保存（组合内的 _settings 是启动时快照）
        composition._settings = ImSettings.Load(composition._workspaceRoot);
        lock (composition._boundGate)
        {
            if (composition._bound.Any(b =>
                composition._settings.Channels.Any(c => c.Type == channelType && c.ChannelId == b.ChannelId)))
            {
                ImDiag.Log($"[im] {channelType} 通道已绑定（无需重复绑定）");
                return true;   // 已绑定
            }
        }
        var channel = composition._settings.Channels.FirstOrDefault(c => c.Type == channelType && c.Enabled);
        if (channel is null)
        {
            ImDiag.Log($"[im] 补绑定失败：配置里没有启用的 {channelType} 通道（请检查 im/config.json）");
            BindErrors.Add($"通道 {channelType}：配置缺失，未绑定");
            return false;
        }
        bool alreadyBound;
        lock (composition._boundGate)
        {
            var existing = composition._bound.FirstOrDefault(b => b.ChannelId == channel.ChannelId);
            alreadyBound = existing != default && existing.Fingerprint == ConfigFingerprint(channel);
        }
        if (alreadyBound)
        {
            ImDiag.Log($"[im] {channelType} 通道已绑定且配置未变（无需重复绑定）");
            return true;
        }
        try
        {
            await composition.BindChannelAsync(channel).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            ImDiag.Log($"[im] 通道补绑定失败 {channelType}：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 启动通道的登录能力实例（扫码类通道：协议在插件内，宿主只取契约驱动）：
    /// 能力 id 由通道类型推导（weixin → weixin-login）；实例 id 与通道一致（共享凭据命名空间）。
    /// </summary>
    public static async Task<IInteractiveLoginProvider?> StartLoginProviderAsync(string channelType,
        string instanceId, IEnumerable<string> outboundHosts, CancellationToken ct = default)
    {
        var capabilityId = channelType switch
        {
            "weixin" => "weixin-login",
            _ => null,
        };
        if (capabilityId is null)
        {
            ImDiag.Log($"[im] 登录失败：通道类型 {channelType} 无登录能力");
            return null;
        }

        // 首选：组合已启动 → 宿主里注册的工厂
        var composition = Instance;
        if (composition?._pluginHost is not null)
        {
            ImDiag.Log($"[im] 正在启动登录能力：{capabilityId}（instance={instanceId}）；已注册能力=[{string.Join(",", composition._pluginHost.ListCapabilityIds())}]");
            try
            {
                var record = await composition._pluginHost.StartInstanceAsync(capabilityId, instanceId,
                    outboundHosts.ToList(), ct).ConfigureAwait(false);
                if (record.Capability is not IInteractiveLoginProvider provider)
                {
                    ImDiag.Log($"[im] 登录失败：能力 {capabilityId} 未实现 IInteractiveLoginProvider（插件 DLL 版本不对？）");
                    return null;
                }
                ImDiag.Log("[im] 登录能力已就绪，开始扫码流程");
                return provider;
            }
            catch (Exception ex)
            {
                ImDiag.Log($"[im] 组合内登录能力启动失败（将尝试独立登录宿主）：{ex.Message}");
            }
        }

        // 兜底：组合未启动（安装版全新工作区，ImEnabled=false 零副作用关闭）或组合内启动失败——
        // 用独立轻宿主加载 weixin-login 能力：用户扫码登录不需要全组合，只要协议登录 + 凭据落库
        var ws = s_workspaceRoot ?? UiPrefs.WorkspaceDir();
        var imDir = Path.Combine(ws, "im");
        Directory.CreateDirectory(imDir);
        var pluginDir = FindInstalledPluginDir(ws, "im-weixin");
        if (pluginDir is null)
        {
            ImDiag.Log($"[im] 登录失败：插件 im-weixin 未安装（plugins 目录无该插件）");
            return null;
        }
        ImDiag.Log($"[im] 独立登录宿主加载：{pluginDir}");
        try
        {
            var (factories, _) = PluginPackageLoader.LoadFactories(pluginDir);
            var loginFactory = factories.FirstOrDefault(f => f.CapabilityId == capabilityId)
                ?? throw new InvalidOperationException($"插件包内无 {capabilityId} 能力（DLL 版本不对？）");
            var hostDb = new HostDatabase(imDir);   // ⚠ 生命周期：登记进 s_standaloneHostDbs，登录提供者存活期间不可释放
            var activations = new ActivationRegistry();
            var activation = activations.Issue();
            var instance = new InstanceRef(loginFactory.PluginId, capabilityId, instanceId);
            var audit = new AuditLog(Path.Combine(imDir, "audit.jsonl"));
            var services = InstanceServices.ForChannelPlugin(hostDb, activations, instance, activation,
                imDir, outboundHosts.ToList(), audit);
            var context = new ExtensionContextImpl(instance, activation, services, HostingContract.Version,
                () => activations.IsAlive(activation), audit);
            var creationContext = new InstanceCreationContext(instance, activation, context);
            var capability = await loginFactory.CreateAsync(creationContext, ct).ConfigureAwait(false);
            if (capability is not IInteractiveLoginProvider provider)
            {
                await hostDb.DisposeAsync().ConfigureAwait(false);   // 形态不对：无人引用，立即释放
                ImDiag.Log($"[im] 登录失败：能力 {capabilityId} 未实现 IInteractiveLoginProvider");
                return null;
            }
            lock (s_standaloneDbGate)
            {
                var key = $"{capabilityId}+{instanceId}";
                if (s_standaloneHostDbs.Remove(key, out var previous))
                {
                    try { previous.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
                }
                s_standaloneHostDbs[key] = hostDb;
            }
            ImDiag.Log("[im] 独立登录宿主就绪（HostDatabase 保持存活至组合启动/进程退出）");
            return provider;
        }
        catch (Exception ex)
        {
            ImDiag.Log($"[im] 独立登录宿主失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>已安装插件目录定位：plugins/{id}/ 下最新含 manifest 的叶子（与 IM 组合包加载同规则）。</summary>
    internal static string? FindInstalledPluginDir(string workspaceRoot, string packageId)
    {
        var root = Path.Combine(workspaceRoot, "plugins", packageId);
        if (!Directory.Exists(root)) return null;
        string? best = null;
        var bestUtc = DateTime.MinValue;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!File.Exists(Path.Combine(dir, "manifest.json"))
                && !Directory.EnumerateFiles(dir, "AgentBrook.Plugin.*.dll").Any()) continue;
            var utc = Directory.GetLastWriteTimeUtc(dir);
            if (utc > bestUtc) { bestUtc = utc; best = dir; }
        }
        return best;
    }

    /// <summary>
    /// 登录完成落配置（宿主职责）：写通道声明（secretRef 已由插件写入凭据库）+ ImEnabled → 组合启动热绑定。
    /// </summary>
    public static async Task<string> ApplyLoginCompletionAsync(string channelType, LoginCompletion completion,
        string? displayName = null, string? channelId = null, string? instanceId = "inst-1")
    {
        var ws = s_workspaceRoot ?? UiPrefs.WorkspaceDir();
        Directory.CreateDirectory(Path.Combine(ws, "im"));

        var settings = ImSettings.Load(ws);
        settings.ImEnabled = true;
        var channel = settings.Channels.FirstOrDefault(c => c.Type == channelType);
        var previousAccount = channel?.AccountId;
        if (channel is null)
        {
            settings.Channels.Add(new ImChannelConfig
            {
                Type = channelType,
                ChannelId = channelId ?? channelType,
                InstanceId = instanceId ?? "inst-1",
                Enabled = true,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? channelType : displayName!,
                SecretRef = completion.SecretRef,
                BaseUrl = completion.BaseUrl,
                AccountId = completion.AccountId,
            });
        }
        else
        {
            channel.SecretRef = completion.SecretRef;
            channel.BaseUrl = completion.BaseUrl ?? channel.BaseUrl;
            channel.AccountId = completion.AccountId ?? channel.AccountId;
            channel.Enabled = true;
            if (!string.IsNullOrWhiteSpace(displayName)) channel.DisplayName = displayName!;
        }
        settings.Save(ws);
        ImDiag.Log($"[im] 登录结果已保存：{channelType} → baseUrl={completion.BaseUrl}，" +
                                $"account={completion.AccountId}，secretRef={completion.SecretRef}");

        // 换号迁移（重新扫码会换 bot 账号）：不迁移则准入 AccountChanged 会拒收全部入站，
        // 且用户需重新配对、会话映射丢失。迁移 identities/routes 的 account_id 到新账号。
        if (!string.IsNullOrWhiteSpace(previousAccount) && previousAccount != completion.AccountId
            && !string.IsNullOrWhiteSpace(completion.AccountId))
        {
            var messaging = Instance?.MessagingInternal;
            if (messaging is not null)
            {
                try
                {
                    var moved = await messaging.MigrateChannelAccountAsync(
                        channelId ?? channelType, instanceId ?? "inst-1", completion.AccountId!).ConfigureAwait(false);
                    ImDiag.Log($"[im] 账号已更换（{previousAccount} → {completion.AccountId}），迁移配对/路由 {moved} 行");
                }
                catch (Exception ex)
                {
                    ImDiag.Log($"[im] 账号迁移失败（将按新会话处理）：{ex.Message}");
                }
            }
        }

        var boundNow = await EnsureChannelBoundAsync(channelType).ConfigureAwait(false);
        ImDiag.Log($"[im] 登录后补绑定 {channelType}：{(boundNow ? "成功" : "失败")}");
        var total = Instance?.BoundInternal.Count ?? 0;
        return boundNow
            ? $"{displayName ?? channelType} 已启用（{total} 通道运行）"
            : $"{displayName ?? channelType} 登录已保存，但通道未绑定成功——请查看 IM 板块运行态与日志";
    }

    /// <summary>桥探活（设置页"测试连接"）：GET {url}/health，带 X-Im-Bridge-Token；返回账号 id。</summary>
    public static async Task<(bool Ok, string Detail)> VerifyBridgeAsync(string bridgeUrl, string token)
    {
        try
        {
            var baseUrl = (bridgeUrl ?? "").Trim().TrimEnd('/');
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
                return (false, "桥地址不是合法 URL（示例：http://127.0.0.1:38100）");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/health");
            req.Headers.TryAddWithoutValidation("X-Im-Bridge-Token", token ?? "");
            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, $"桥或 Token 不可用（HTTP {(int)resp.StatusCode}）");
            string? accountId = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                accountId = doc.RootElement.TryGetProperty("accountId", out var a) ? a.GetString() : null;
            }
            catch (JsonException) { /* 只要 2xx 即视为可达；账号字段缺失不算错误 */ }
            return (true, string.IsNullOrWhiteSpace(accountId) ? "桥可达" : $"账号 {accountId}");
        }
        catch (Exception ex)
        {
            return (false, $"连接失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 桥引导设置（设置页"保存并启用"）：写入宿主 secret + 更新 config（ImEnabled=true + bridge 通道
    /// + BridgeUrl）→ 确保组合启动 → 热绑定通道（已运行则免重启即时生效）。返回状态文案。
    /// </summary>
    public static async Task<string> SetupBridgeAsync(string bridgeUrl, string token, string? displayName = null)
    {
        var ws = s_workspaceRoot ?? UiPrefs.WorkspaceDir();
        var imDir = Path.Combine(ws, "im");
        Directory.CreateDirectory(imDir);
        var baseUrl = (bridgeUrl ?? "").Trim().TrimEnd('/');

        // 1) secret 入宿主凭据库（不落明文 config；命名空间须与桥插件实例一致 im-bridge+inst-1）
        var hostDb = new HostDatabase(imDir);
        await using var _ = hostDb.ConfigureAwait(false);
        var activations = new ActivationRegistry();
        var activation = activations.Issue();
        var credentials = new CredentialService(hostDb, "im-bridge+inst-1", activation, activations);
        var secretRef = await credentials.SetSecretAsync("bridge-token", token, CancellationToken.None).ConfigureAwait(false);

        // 2) config 更新（保持既有通道顺序，桥通道就地新增/更新）
        var settings = ImSettings.Load(ws);
        settings.ImEnabled = true;
        var channel = settings.Channels.FirstOrDefault(c => c.Type == "bridge");
        if (channel is null)
        {
            settings.Channels.Add(new ImChannelConfig
            {
                Type = "bridge", ChannelId = "bridge-wx", InstanceId = "inst-1",
                Enabled = true, DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Bridge" : displayName!,
                SecretRef = secretRef, BridgeUrl = baseUrl,
            });
        }
        else
        {
            channel.SecretRef = secretRef;
            channel.BridgeUrl = baseUrl;
            channel.Enabled = true;
            if (!string.IsNullOrWhiteSpace(displayName)) channel.DisplayName = displayName!;
        }
        settings.Save(ws);

        // 3) 组合启动（幂等）+ 补绑定
        var boundNow = await EnsureChannelBoundAsync("bridge").ConfigureAwait(false);
        var total = Instance?.BoundInternal.Count ?? 0;
        return boundNow
            ? $"Bridge 已启用（{total} 通道运行）"
            : "Bridge 配置已保存，但通道未绑定成功——请查看 IM 板块运行态与日志";
    }

    /// <summary>
    /// Telegram 引导设置（设置页"保存并启用"）：写入宿主 secret + 更新 config（ImEnabled=true +
    /// telegram 通道）→ 确保组合启动 → 热绑定通道（已运行则免重启即时生效）。返回状态文案。
    /// </summary>
    public static async Task<string> SetupTelegramAsync(string token)
    {
        // 服务器 index 可能缺 category 字段——显式声明 IM 插件 id → category 映射
        if (Instance is not null && Instance._pluginHost is not null)
        {
            // 会在 RegisterImCategoryLoader 的 agent.PluginManager 上生效（Agent 就绪后调用）
        }
        var ws = s_workspaceRoot ?? UiPrefs.WorkspaceDir();
        var imDir = Path.Combine(ws, "im");
        Directory.CreateDirectory(imDir);

        // 1) secret 入宿主凭据库（不落明文 config）
        var hostDb = new HostDatabase(imDir);
        await using var _ = hostDb.ConfigureAwait(false);
        var activations = new ActivationRegistry();
        var activation = activations.Issue();
        var credentials = new CredentialService(hostDb, "im-telegram+inst-1", activation, activations);
        var secretRef = await credentials.SetSecretAsync("bot-token", token, CancellationToken.None).ConfigureAwait(false);

        // 2) config 更新（ImEnabled=true + telegram 通道声明）
        var settings = ImSettings.Load(ws);
        settings.ImEnabled = true;
        if (!settings.Channels.Any(c => c.Type == "telegram"))
        {
            settings.Channels.Add(new ImChannelConfig
            {
                Type = "telegram", ChannelId = "telegram", InstanceId = "inst-1",
                Enabled = true, DisplayName = "Telegram", SecretRef = secretRef,
            });
        }
        else
        {
            settings.Channels[0].SecretRef = secretRef;
            settings.Channels[0].Enabled = true;
        }
        settings.Save(ws);

        // 3) 组合启动（幂等）+ 补绑定（组合已在运行时 EnsureStarted 不会重扫通道）
        var boundNow = await EnsureChannelBoundAsync("telegram").ConfigureAwait(false);
        var total = Instance?.BoundInternal.Count ?? 0;
        return boundNow
            ? $"Telegram 已启用（{total} 通道运行）"
            : "Telegram 配置已保存，但通道未绑定成功——请查看 IM 板块运行态与日志";
    }

    /// <summary>把 "im" 类别加载器注册进旧 PluginManager（安装分流：IM 插件经新宿主加载）。
    /// 不依赖 Instance——注册时只需 agent.PluginManager；loader 回调在安装触发时才解析 Instance。</summary>
    /// <summary>
    /// 加载包内全部能力工厂（带重试）：刚装完/刚解压时文件可能仍在写入或被旧 ALC 占用，
    /// 直接做哈希校验会误报"entry hash 不符（篡改/损坏）"（用户实测：第一次装失败、再点一次成功）。
    /// </summary>
    private static async Task<IReadOnlyList<ICapabilityFactory>> LoadFactoriesWithRetryAsync(string dir)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var (factories, _) = PluginPackageLoader.LoadFactories(dir);
                return factories;
            }
            catch (PluginPackageException ex) when (attempt < 3 && ex.Code == ImErrorCodes.BadPayload)
            {
                ImDiag.Log($"[im] 包校验瞬时失败（第 {attempt + 1} 次，重试）：{ex.Message}");
                await Task.Delay(300).ConfigureAwait(false);
            }
        }
    }

    /// <summary>已安装但 IM 宿主尚未就绪的包目录（组合启动时补加载）。</summary>
    private static readonly List<string> s_pendingPackages = [];

    /// <summary>分类加载器是否已注册（避免重复注册/重复后台重试）。</summary>
    private static bool s_categoryLoaderRegistered;

    /// <summary>等 Agent 就绪后注册 IM 分类加载器（安装分流依赖；Agent 未就绪时后台重试）。
    /// 不依赖 Instance——loader 回调在安装触发时才解析宿主。</summary>
    private static void RegisterImCategoryLoader()
    {
        if (s_categoryLoaderRegistered) return;
        var agent = s_agentAccessor?.Invoke();
        if (agent?.PluginManager is not null)
        {
            Register(agent);
            return;
        }
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 60 && !s_categoryLoaderRegistered; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var ready = s_agentAccessor?.Invoke();
                if (ready?.PluginManager is not null)
                {
                    Register(ready);
                    return;
                }
            }
            ImDiag.Log("[im] IM 分类加载器注册放弃（Agent 长时间未就绪）——IM 插件安装会走旧生命周期并失败");
        });

        static void Register(AgentBrook.Agent.BrookAgent readyAgent)
        {
            readyAgent.PluginManager!.RegisterCategoryLoader("im", async dir =>
            {
                // 一个包可含多个能力工厂（如微信：weixin-channel + weixin-login）
                var factories = await LoadFactoriesWithRetryAsync(dir).ConfigureAwait(false);
                ImDiag.Log($"[im] 包加载：{dir} → 能力 [{string.Join(",", factories.Select(f => f.CapabilityId))}]");
                var host = Instance?._pluginHost;
                if (host is null)
                {
                    // 组合未启动：记下目录，组合启动时补注册（否则登录/通道能力都不可用）
                    lock (s_pendingPackages) s_pendingPackages.Add(dir);
                    await Console.Error.WriteLineAsync($"[im] IM 宿主未启动，包已登记待加载：{dir}");
                    return;
                }
                foreach (var factory in factories) host.RegisterFactory(factory);
                await Console.Error.WriteLineAsync(
                    $"[im] 插件已由 IM 宿主加载（category=im，{factories.Count} 个能力）：{dir}");
            });
            // 卸载分流：先由 IM 宿主释放（停实例 + 注销工厂 + 卸 ALC），否则 Windows 上 DLL 被占用无法删除
            readyAgent.PluginManager!.RegisterCategoryUnloader("im", async pluginId =>
            {
                var host = Instance?._pluginHost;
                if (host is null) return;
                try
                {
                    var removed = await host.UnloadPluginAsync(pluginId, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    // ALC 为 collectible：工厂引用释放后需 GC 才能卸载程序集（DLL 文件锁随之释放）
                    for (var i = 0; i < 3; i++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        await Task.Delay(120).ConfigureAwait(false);
                    }
                    ImDiag.Log($"[im] 插件已从 IM 宿主卸载：{pluginId}（注销 {removed} 个能力）");
                }
                catch (Exception ex)
                {
                    ImDiag.Log($"[im] 插件卸载失败 {pluginId}：{ex.Message}");
                }
            });
            s_categoryLoaderRegistered = true;
            ImDiag.Log("[im] IM 分类加载器已注册（IM 插件安装将分流到 IM 宿主）");
        }
    }

    private ImComposition(string workspaceRoot, Func<AgentBrook.Agent.BrookAgent?> agentAccessor,
        ImGateway gateway, ImSettings settings, string pluginsRoot)
    {
        _workspaceRoot = workspaceRoot;
        _agentAccessor = agentAccessor;
        _gateway = gateway;
        _settings = settings;
        _pluginsRoot = pluginsRoot;
    }

    /// <summary>组合实例（组合根 StartAsync 创建；设置页经静态 EnsureStartedAsync/SetupTelegramAsync 访问）。</summary>
    public static ImComposition? Instance { get; private set; }

    /// <summary>最近一次启动的包加载/通道绑定失败明细（设置页运行态行下方展示；空=全部成功）。</summary>
    public static List<string> BindErrors { get; } = [];

    /// <summary>最近一次 IM 回合失败原因（设置页 IM 板块红字展示；null=无未恢复失败）。</summary>
    public static string? LastTurnError { get; private set; }

    /// <summary>IM 回合 UI 钩子（App 组合根注入：气泡状态 + 选中会话流式。null=无 UI，纯后台）。</summary>
    public static Action<string, ImTurnUiKind, string?>? TurnUiHook { get; set; }

    /// <summary>
    /// IM 回合<b>提交完成后</b>钩子（App 组合根注入）。注意与 TurnUiHook 的 Completed 区分：
    /// 后者在执行器返回时触发（提交前），此处才保证 CURRENT 已切换——UI 重载内存态必须挂这里。
    /// 参数：(sessionId, completed, error)。
    /// </summary>
    public static Action<string, bool, string?>? TurnCommittedHook { get; set; }

    /// <summary>
    /// 主窗口 IM 会话回合结果 → 该会话的 IM 通道（双向闭环；设计要求：IM 请求的结果、
    /// 用户在该 IM 会话中请求的结果，都发送给该 IM）。非 IM 会话 / 组合未启动 no-op，不抛。
    /// 供 AssistantController 主回合结束时调用（fire-and-forget）。
    /// </summary>
    public static void TryDeliverToIm(string sessionId, string answer)
    {
        var messaging = Instance?.MessagingInternal;
        if (messaging is null) return;
        try
        {
            if (messaging.EnqueueOutbound(sessionId, answer))
                ImDiag.Log($"[im] 主窗口回合结果已入队送达 {sessionId}（{answer.Length} 字符）");
        }
        catch (Exception ex)
        {
            ImDiag.Log($"[im] 主窗口结果投递失败 {sessionId}：{ex.Message}");
        }
    }

    /// <summary>
    /// IM 会话删除（UI 删除 im- 前缀会话时由 BrookAgent.DeleteSessionAsync 调用）：
    /// 走 IM 领域删除协议（墓碑落库 + 版本目录清理）+ 路由解绑——之后同 chat 新消息
    /// 命中墓碑分支 → 解绑 → 开全新会话（删除=归档）。组合未启动 no-op，不抛。
    /// </summary>
    public static async Task DeleteImSessionDataAsync(string sessionId)
    {
        var messaging = Instance?.MessagingInternal;
        if (messaging is null || !sessionId.StartsWith("im-", StringComparison.Ordinal)) return;
        try
        {
            await messaging.DeleteSessionAsync(sessionId).ConfigureAwait(false);
            ImDiag.Log($"[im] IM 会话已删除（墓碑+路由解绑）：{sessionId}");
        }
        catch (Exception ex)
        {
            ImDiag.Log($"[im] IM 会话删除失败 {sessionId}：{ex.Message}");
        }
    }

    /// <summary>静态工作区根（登录/设置页等静态入口用；未启动时回退 UiPrefs）。</summary>
    internal static string? StaticWorkspaceRoot => s_workspaceRoot;

    /// <summary>清空通道状态快照（测试/重启组合时）。</summary>

    /// <summary>各通道最近状态（UI 展示用：id → (state, reason)）。</summary>
    private static readonly Dictionary<string, (string State, string? Reason)> s_channelStatus = [];
    internal static List<(string Id, string State, string? Reason)> ChannelStatuses
    {
        get { lock (s_channelStatus) return [.. s_channelStatus.Select(kv => (kv.Key, kv.Value.State, kv.Value.Reason))]; }
    }

    /// <summary>独立登录宿主的 HostDatabase 注册表：登录提供者长生命周期引用它，
    /// 在组合启动前**不可释放**（释放即 ExecuteNonQuery on closed connection 崩溃）。</summary>
    private static readonly object s_standaloneDbGate = new();
    private static readonly Dictionary<string, HostDatabase> s_standaloneHostDbs = new();

    /// <summary>关闭全部独立登录宿主库（仅测试重置/进程收尾调用；正常运行保持存活供登录提供者落凭据）。</summary>
    private static void CloseStandaloneHostDbs()
    {
        lock (s_standaloneDbGate)
        {
            foreach (var db in s_standaloneHostDbs.Values)
            {
                try { db.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            }
            s_standaloneHostDbs.Clear();
        }
    }

    /// <summary>插件宿主（静态类别加载器用；未启动为 null）。</summary>
    internal PluginHost? PluginHostInternal => _pluginHost;
    internal MessagingService? MessagingInternal => _messaging;
    internal List<(string ChannelId, string InstanceId)> BoundInternal =>
        [.. _bound.Select(b => (b.ChannelId, b.InstanceId))];

    private static string? s_workspaceRoot;
    private static Func<AgentBrook.Agent.BrookAgent?>? s_agentAccessor;
    private static ImGateway? s_gateway;
    private static string? s_pluginsRootOverride;

    /// <summary>静态幂等启动（组合根开机调用；设置页保存后再次调用即热启）。disabled → 零副作用。</summary>
    public static async Task<ImCompositionResult> StartAsync(
        string workspaceRoot, Func<AgentBrook.Agent.BrookAgent?> agentAccessor, ImGateway gateway,
        string? pluginsRootOverride = null)
    {
        s_workspaceRoot = workspaceRoot;
        s_agentAccessor = agentAccessor;
        s_gateway = gateway;
        s_pluginsRootOverride = pluginsRootOverride;
        return await EnsureStartedAsync().ConfigureAwait(false);
    }

    /// <summary>测试间重置静态组合状态（U01 隔离：前测留下的 Instance 会让 disabled 分支返回 enabled）。</summary>
    internal static async Task ResetForTestsAsync()
    {
        if (Instance is not null)
        {
            foreach (var d in Instance._disposables)
            {
                try { await d.DisposeAsync().ConfigureAwait(false); }
                catch { /* 测试清理尽力而为 */ }
            }
            Instance = null;
        }
        s_workspaceRoot = null;
        s_agentAccessor = null;
        s_gateway = null;
        s_pluginsRootOverride = null;
        BindErrors.Clear();
        LastTurnError = null;
        TurnUiHook = null;
        TurnCommittedHook = null;
        lock (s_pendingPackages) s_pendingPackages.Clear();
        lock (s_channelStatus) s_channelStatus.Clear();
        CloseStandaloneHostDbs();
        s_categoryLoaderRegistered = false;
    }

    /// <summary>静态幂等启动（设置页保存后调用同一入口）。disabled → 零副作用返回；enabled → 完整组合。</summary>
    public static async Task<ImCompositionResult> EnsureStartedAsync()
    {
        RegisterImCategoryLoader();
        if (Instance is not null)
        {
            return new ImCompositionResult(true, "{\"enabled\":true}", s_gateway, Instance.MessagingInternal, Instance.BoundInternal.ToList());
        }
        if (string.IsNullOrEmpty(s_workspaceRoot) || s_agentAccessor is null || s_gateway is null)
        {
            return new ImCompositionResult(false, "{\"enabled\":false}", null, null, []);
        }
        var settings = ImSettings.Load(s_workspaceRoot);
        ImDiag.Log($"[im-compose] ImEnabled={settings.ImEnabled}, ws={s_workspaceRoot}");
        if (!settings.ImEnabled)
        {
            // U01：默认关闭 = 零副作用。不创建 im/ 目录、不注册路由、不启动恢复/通道、不迁移会话。
            ImDiag.Log("[im-compose] 返回 disabled（零副作用）");
            return new ImCompositionResult(false, "{\"enabled\":false}", null, null, []);
        }

        var pluginsRoot = s_pluginsRootOverride ?? Path.Combine(s_workspaceRoot, "im", "plugins");
        var composition = new ImComposition(s_workspaceRoot, s_agentAccessor, s_gateway, settings, pluginsRoot);
        Instance = composition;
        return await composition.StartEnabledAsync().ConfigureAwait(false);
    }

    private async Task<ImCompositionResult> StartEnabledAsync()
    {
        var imDir = Path.Combine(_workspaceRoot, "im");
        Directory.CreateDirectory(imDir);
        ImDiag.Init(_workspaceRoot);   // 仅启用时初始化诊断日志（关闭状态零副作用）
        PluginHost.DiagnosticsSink = ImDiag.Log;   // 插件日志与宿主日志同文件（用户一处可见）
        BindErrors.Clear();
        LastTurnError = null;
        ImDiag.Log("[im] IM 构建标记：R5-20261105（微信通道 + 补绑定 + 宿主库并发修复）"
            + "——若未见此行说明跑的是旧构建");
        ImDiag.Log("[im] 配置通道：" + string.Join("、", _settings.Channels.Select(c =>
            $"{c.Type}({c.ChannelId}, enabled={c.Enabled}, baseUrl={(string.IsNullOrEmpty(c.BaseUrl) ? "-" : c.BaseUrl)}, "
            + $"account={(string.IsNullOrEmpty(c.AccountId) ? "-" : c.AccountId)}, secret={(string.IsNullOrEmpty(c.SecretRef) ? "-" : "有")})")));
        // transcript.jsonl 单一写者=BrookAgent 游标机制；IM 提交的投影写入保持启用：
        // 事件已是全文对（user/assistant 各一条、与 MAF 历史精确一致），投影行与对账签名天然兼容；
        // 附件回执回合不跑模型（轻量确认轮），唯一落 transcript 的路径就是投影

        // 版本化模式：写 versioned.flag（模式由工作区标记固定，不随开关反复切换）
        if (_settings.StorageMode == "versioned")
        {
            File.WriteAllText(Path.Combine(imDir, WorkspaceStorageModeDetector.FlagFileName),
                DateTimeOffset.UtcNow.ToString("O"));
        }

        var db = await ImDatabase.OpenAsync(imDir).ConfigureAwait(false);
        _db = db;
        _disposables.Add(db);
        var fs = new SessionFileSystem(_workspaceRoot);
        var journal = new CommitJournal(db, fs);
        var coordinator = new SessionExecutionCoordinator(fs);
        var delete = new DeleteProtocol(db, fs, journal);
        var recovery = new RecoveryService(db, fs, journal, delete);
        _store = new VersionedSessionStore(fs, journal, coordinator, recovery);

        // 版本化持久化附加到 BrookAgent（保存/载入/切换走 CURRENT 提交单元）。
        // 组合启动早于 Agent 就绪——eager `agent?.Attach` 会静默跳过且永不重试（运行时重载/版本化保存
        // 全部失效，重启后才经 legacy session.json 镜像看到内容）。改为就绪后延迟附加。
        _ = Task.Run(AttachPersistenceWhenAgentReadyAsync);

        // 启动恢复对账（取消意图优先 → 补切/补投影 → 状态矩阵）
        var report = await recovery.RecoverAsync().ConfigureAwait(false);
        ImDiag.Log($"[im] recovery: {report.Actions.Count} actions, recoveryRequired={report.RecoveryRequiredReasons.Count}");

        // 通道插件包加载（主应用不静态引用插件实现）。
        // 包来源统一：直接扫 PluginManager 安装目录（workspace/plugins/{id}/ 最新叶子）——不依赖 Agent
        // 就绪时机（组合开机启动早于 Agent 就绪，ListInstalled 会拿到 null）；im/plugins/{id} 手工布点回退。
        _pluginHost = new PluginHost(new HostDatabase(imDir), imDir);
        _disposables.Add(_pluginHost);
        foreach (var (packageId, capabilityId) in new[]
                 {
                     ("im-telegram", "telegram-channel"),
                     ("im-bridge", "bridge-channel"),
                     ("im-weixin", "weixin-channel"),
                 })
        {
            var dir = ResolveChannelPackageDir(packageId);
            if (dir is null) continue;
            try
            {
                // 一个包可含多个能力工厂（如微信：weixin-channel + weixin-login）——全部注册
                var factories = await LoadFactoriesWithRetryAsync(dir).ConfigureAwait(false);
                ImDiag.Log($"[im] 包加载：{dir} → 能力 [{string.Join(",", factories.Select(f => f.CapabilityId))}]");
                foreach (var factory in factories) _pluginHost.RegisterFactory(factory);
            }
            catch (Exception ex)
            {
                BindErrors.Add($"包加载 {packageId}：{ex.Message}");
                ImDiag.Log($"[im] 包加载失败 {packageId}：{ex.Message}");
            }
        }

        // 补加载安装时宿主尚未就绪的包（分类加载器登记的目录）
        List<string> pending;
        lock (s_pendingPackages)
        {
            pending = [.. s_pendingPackages];
            s_pendingPackages.Clear();
        }
        foreach (var dir in pending)
        {
            try
            {
                var factories = await LoadFactoriesWithRetryAsync(dir).ConfigureAwait(false);
                ImDiag.Log($"[im] 包加载：{dir} → 能力 [{string.Join(",", factories.Select(f => f.CapabilityId))}]");
                foreach (var factory in factories) _pluginHost.RegisterFactory(factory);
                ImDiag.Log($"[im] 待加载包已补注册（{factories.Count} 个能力）：{dir}");
            }
            catch (Exception ex)
            {
                ImDiag.Log($"[im] 待加载包注册失败 {dir}：{ex.Message}");
            }
        }

        // 会话工厂：IM 通信对象 → 独立版本化会话（真实 AIAgent 种子化）。
        // legacy 注册走惰性解析（组合启动早于 Agent 就绪——eager 捕获会拿到 null 永久失去注册能力）；
        // agent.LegacySessions 单例 index 写者，勿另建 SessionStore。
        _agentHost = new HelperImAgentHost(_workspaceRoot);
        var agentHost = _agentHost;
        var sessions = new HelperImSessionFactory(_workspaceRoot, _store, agentHost,
            legacyRegister: (id, title) => LegacyStore()?.Create(id, title),
            legacyMirrorDir: Path.Combine(_workspaceRoot, "sessions"));
        var messaging = new MessagingService(db, sessions,
            _ => new HelperImTurnExecutor(_store, agentHost,
                hooks: UiHooks(),
                legacySessionsDir: Path.Combine(_workspaceRoot, "sessions"),
                brookAccessor: () => _agentAccessor(),   // 同权执行：走主 Agent 完整回合循环（审批弹窗与主界面同流程）
                blobs: new HelperBlobService(_workspaceRoot),   // 产物回传写 Blob（附件互传出站）
                materializeAttachment: (att, token) =>
                    MaterializeImAttachmentAsync(_messaging, new HelperBlobService(_workspaceRoot), att, token)),
            new HelperApprovalPort(), Caps(), workspaceRootOverride: _workspaceRoot);
        await messaging.StartAsync().ConfigureAwait(false);
        _messaging = messaging;

        // 提交完成后通知 UI 重载（CURRENT 已切换——执行器返回≠提交完成，重载必须挂这里）
        messaging.Dispatcher.TurnCompleted += notice =>
        {
            LegacyStore()?.Touch(notice.SessionId);   // 列表时间戳只在真实提交后推进（执行器 Touch 预提交会"假活跃"）
            LastTurnError = notice.Completed ? null : (notice.Error ?? "回合失败（原因未返回）");
            TurnCommittedHook?.Invoke(notice.SessionId, notice.Completed, notice.Error);
        };

        // 存量 IM 会话补注册（旧二进制期间创建的会话 UI 不可见；Agent 未就绪时自动重试）
        _ = Task.Run(BackfillLegacySessionsAsync);

        // 调试密钥种子化（仅调试工作区；正式部署走设置 UI——U02 持久化不落明文）。
        // 命名空间取 secretRef 的 ns 段（"{pluginId}+{instanceId}/{name}"）——固定 ns 会让 telegram 密钥落到 bridge 名下读不到。
        var hostDb = new HostDatabase(imDir);
        var activations2 = new ActivationRegistry();
        var act2 = activations2.Issue();
        foreach (var (secretRef, value) in _settings.DebugSecrets)
        {
            var parts = secretRef.Split('/', 2);
            if (parts.Length != 2) continue;
            if (!parts[0].Contains('+')) continue;   // ns 段须形如 "{pluginId}+{instanceId}"
            var nsCreds = new CredentialService(hostDb, parts[0], act2, activations2);
            nsCreds.SetSecretAsync(parts[1], value, CancellationToken.None).GetAwaiter().GetResult();
        }

        // 通道组合：配置驱动
        foreach (var ch in _settings.Channels.Where(c => c.Enabled))
        {
            try
            {
                await BindChannelAsync(ch).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                BindErrors.Add($"通道 {ch.ChannelId}：{ex.Message}");
                ImDiag.Log($"[im] 通道 {ch.ChannelId} 启动失败：{ex.Message}");
            }
        }

        var state = JsonSerializer.Serialize(new
        {
            enabled = true,
            channels = _bound.Select(b => new { b.ChannelId, b.InstanceId }).ToList(),
            recoveryRequired = report.RecoveryRequiredReasons.Count,
        });
        // 独立登录宿主的库**不在此关闭**：登录提供者（插件内）可能仍在轮询/落凭据，
        // 提前释放会造成 ExecuteNonQuery on closed connection。同一 能力+实例 下次登录时替换并释放旧连接，
        // 进程退出统一回收；SQLite 多连接访问同一文件本身安全（BusyTimeout 15s）。

        return new ImCompositionResult(true, state, _gateway, _messaging,
            [.. _bound.Select(b => (b.ChannelId, b.InstanceId))]);
    }

    /// <summary>解析通道包目录：workspace/plugins/{id}/ 最新叶子目录优先（设置页安装落位，纯磁盘扫描——
    /// 不依赖 Agent 就绪）；workspace/im/plugins/{id}/ 回退（手工布点）。均无 → null。</summary>
    private string? ResolveChannelPackageDir(string packageId)
    {
        var pluginRoot = Path.Combine(_workspaceRoot, "plugins", packageId);
        if (Directory.Exists(pluginRoot))
        {
            string? best = null;
            var bestUtc = DateTime.MinValue;
            foreach (var d in Directory.EnumerateDirectories(pluginRoot))
            {
                var hasPayload = File.Exists(Path.Combine(d, PluginPackageLoader.ManifestFileName))
                    || Directory.EnumerateFiles(d, "AgentBrook.Plugin.*.dll").Any();
                if (!hasPayload) continue;
                var utc = Directory.GetLastWriteTimeUtc(d);
                if (utc > bestUtc) { bestUtc = utc; best = d; }
            }
            if (best is not null) return best;
        }
        var manual = Path.Combine(_pluginsRoot, packageId);
        return Directory.Exists(manual) ? manual : null;
    }

    /// <summary>通道出站白名单（§15.2 申报制）：回环恒在列；telegram 固定 api.telegram.org；
    /// bridge 取 BridgeUrl 主机（真实桥不一定在本机）。</summary>
    private static List<string> BuildOutboundHosts(ImChannelConfig ch)
    {
        var hosts = new List<string> { "127.0.0.1", "localhost" };
        if (ch.Type == "telegram") hosts.Add("api.telegram.org");
        if (ch.Type == "weixin")
        {
            hosts.Add("ilinkai.weixin.qq.com");
            hosts.Add("novac2c.cdn.weixin.qq.com");   // 媒体 CDN（图片/语音/文件/视频下载）
            if (!string.IsNullOrWhiteSpace(ch.BaseUrl) && Uri.TryCreate(ch.BaseUrl, UriKind.Absolute, out var wu))
                hosts.Add(wu.Host);
        }
        if (!string.IsNullOrWhiteSpace(ch.BridgeUrl) && Uri.TryCreate(ch.BridgeUrl, UriKind.Absolute, out var u))
            hosts.Add(u.Host);
        return hosts;
    }

    private async Task BindChannelAsync(ImChannelConfig ch)
    {
        if (_messaging is null || _pluginHost is null)
        {
            ImDiag.Log($"[im] 跳过绑定 {ch.ChannelId}：组合未就绪（messaging={_messaging is not null}, host={_pluginHost is not null}）");
            BindErrors.Add($"通道 {ch.ChannelId}：IM 组合未就绪，未绑定");
            return;
        }
        var capabilityId = ch.Type switch
        {
            "telegram" => "telegram-channel",
            "bridge" => "bridge-channel",
            "weixin" => "weixin-channel",
            _ => throw new InvalidOperationException($"未知通道类型：{ch.Type}"),
        };
        lock (_boundGate)
        {
            if (_bound.Any(b => b.ChannelId == ch.ChannelId && b.InstanceId == ch.InstanceId))
            {
                ImDiag.Log($"[im] 通道 {ch.ChannelId} 已绑定，跳过重复绑定");
                return;
            }
        }
        ImDiag.Log($"[im] 正在绑定通道 {ch.ChannelId}（{ch.Type} → {capabilityId}，白名单：{string.Join(",", BuildOutboundHosts(ch))}）");
        // 账号迁移（幂等）：配置里的账号是最近一次登录结果；库内若仍是旧账号，先改写配对与路由，
        // 否则准入的 AccountChanged 会拒收全部入站（重新扫码换号后必然发生）
        if (!string.IsNullOrWhiteSpace(ch.AccountId))
        {
            try
            {
                var moved = await _messaging.MigrateChannelAccountAsync(ch.ChannelId, ch.InstanceId, ch.AccountId!)
                    .ConfigureAwait(false);
                if (moved > 0) ImDiag.Log($"[im] 绑定前账号对齐：{ch.ChannelId} → {ch.AccountId}（迁移 {moved} 行）");
            }
            catch (Exception ex)
            {
                ImDiag.Log($"[im] 绑定前账号对齐失败：{ex.Message}");
            }
        }

        // 同键旧实例先停（幂等替换；否则旧轮询会带着已撤销的租约继续跑 → 持续报"网络服务未授予"）
        try { await _pluginHost.StopInstanceAsync(capabilityId, ch.InstanceId, TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception ex) { ImDiag.Log($"[im] 停止旧实例失败 {ch.ChannelId}：{ex.Message}"); }

        var record = await _pluginHost.StartInstanceAsync(capabilityId, ch.InstanceId, BuildOutboundHosts(ch),
            CancellationToken.None,
            webhook: new HelperWebhookService(_gateway),
            blob: new HelperBlobService(_workspaceRoot)).ConfigureAwait(false);
        var channel = (IImChannel)record.Capability!;
        channel.StatusChanged += status =>
        {
            lock (s_channelStatus)
                s_channelStatus[ch.ChannelId] = (status.State.ToString(), status.Reason);
        };
        var sink = _messaging.BindChannel(ch.ChannelId, ch.InstanceId, accountEpoch: 1, channel,
            autoPairPrivateChats: true);   // Helper 形态：私聊首条即配对（防无配对 UI 死局）
        await channel.StartAsync(new ImChannelStartContext(sink, JsonSerializer.Serialize(new
        {
            channelId = ch.ChannelId,
            displayName = ch.DisplayName,
            // Telegram 平台基地址（插件设计为必填、不内置真实域名）；bridge/weixin 通道不使用
            apiBase = ch.Type == "telegram" ? "https://api.telegram.org" : "",
            // 微信 iLink：业务节点（登录响应 baseurl）+ 机器人账号 id
            baseUrl = ch.Type == "weixin" ? (ch.BaseUrl ?? "https://ilinkai.weixin.qq.com") : "",
            accountId = ch.Type == "weixin" ? (ch.AccountId ?? "") : "",
            bridgeUrl = ch.BridgeUrl ?? "",
            secretRef = ch.SecretRef,
            pollTimeoutSeconds = 30,
        })), CancellationToken.None).ConfigureAwait(false);
        lock (_boundGate) _bound.Add((ch.ChannelId, ch.InstanceId, ConfigFingerprint(ch)));
        ImDiag.Log($"[im] 通道已绑定：{ch.ChannelId}（{ch.Type}）→ 会话将按对端自动创建");
    }

    /// <summary>TurnUiHook → 执行器钩子适配（无 UI 注入时返回 null，纯后台执行）。</summary>
    private static ImTurnHooks? UiHooks() => TurnUiHook is null ? null : new ImTurnHooks(
        OnTurnStart: id => TurnUiHook!(id, ImTurnUiKind.Started, null),
        OnDelta: (id, text) => TurnUiHook!(id, ImTurnUiKind.Delta, text),
        OnTurnEnd: (id, ok) => TurnUiHook!(id, ok ? ImTurnUiKind.Completed : ImTurnUiKind.Failed, null));

    /// <summary>附件物化（统一管道 §8.1）：通道插件 MaterializeAsync → Blob 回读字节（多模态模型输入用）。</summary>
    private static async Task<TurnExecutionAttachmentBlob?> MaterializeImAttachmentAsync(
        MessagingService? messaging, HelperBlobService blobs, TurnExecutionAttachment att, CancellationToken ct)
    {
        var channel = messaging?.GetChannel(att.ChannelId, att.InstanceId);
        if (channel is null) return null;
        var descriptor = new ImAttachmentDescriptor(att.Provider, 1, att.RemoteId, att.Name, att.MediaType,
            (ImAttachmentKind)att.Kind, att.DeclaredBytes, null, ImAttachmentState.Referenced, null);
        var content = await channel.MaterializeAsync(descriptor, ct).ConfigureAwait(false);
        if (content is null) return null;
        await using var stream = await blobs.OpenReadAsync(content, "turn-attachment", ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
        return new TurnExecutionAttachmentBlob(content.BlobId, ms.ToArray(), content.MediaType, content.FileName, ms.Length, blobs.GetLocalPath(content));
    }

    /// <summary>等 Agent 就绪后附加版本化持久化（eager 附加会因 Agent 未就绪静默丢失——时序坑第 4 犯）。</summary>
    private async Task AttachPersistenceWhenAgentReadyAsync()
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var agent = _agentAccessor();
            if (agent is not null)
            {
                agent.AttachSessionPersistence(_store!);
                ImDiag.Log("[im] 版本化持久化已附加到 BrookAgent");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        ImDiag.Log("[im] 持久化附加放弃（Agent 长时间未就绪——运行时重载/版本化保存将不可用）");
    }

    /// <summary>legacy 会话库惰性解析（每回合/回填时求值——Agent 就绪前为 null 属正常态）。</summary>
    private AgentBrook.Agent.Infrastructure.SessionStore? LegacyStore() => _agentAccessor()?.LegacySessions;

    /// <summary>
    /// ⚠ 已停用（不再从回填调用）：按 MAF 图片字节哈希回填附件行的方案在"同一图片重复发送"
    /// 等场景会匹配失败，把图片改写成 `[附件]` 占位（用户重启后图片消失）。转写现由提交投影
    /// 独占写入（含 artifacts），启动不重写。保留实现仅供测试/排障参考。
    ///
    /// IM 会话 transcript 净化（历史实现）：历史碎片行/重复行（旧逐 delta 事件投影、双写镜像）会让
    /// BrookAgent 转写对账的<b>尾签名永不匹配</b> → 每次切换会话按"游标丢失"把整段 MAF 历史重追加一遍
    /// （对话重复且越切越膨胀）。以 MAF CURRENT 为权威重建为无键 role/text 行——与 BrookAgent 写出格式
    /// 一致（其重载/对账天然匹配）；投影 RepairIn 视无键行为旧记录（不参与补写判定），不会重追加。
    /// </summary>
    internal static void SanitizeImTranscript(string sessionDir, byte[] currentPayload, Microsoft.Agents.AI.AIAgent agent)
    {
        var transcriptPath = Path.Combine(sessionDir, "transcript.jsonl");
        try
        {
            var session = AgentBrook.Agent.IM.MafSnapshotCodec.Decode(agent, currentPayload);
            if (!session.TryGetInMemoryChatHistory(out var history, null, null) || history is null) return;

            // 旧文件中的附件行（带 artifacts）必须保留：图片本体只在这里引用（MAF 侧是 DataContent，
            // 转写不落图片）。配对用**图片字节 SHA256** 精确匹配（顺序排队会错位：早期无 artifacts 的
            // 图片消息会抢占最近的附件行）。
            var artifactBySha = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(transcriptPath))
            {
                foreach (var line in File.ReadLines(transcriptPath))
                {
                    var t = line.Trim();
                    if (t.Length == 0) continue;
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(t);
                        if (!doc.RootElement.TryGetProperty("artifacts", out var a)
                            || a.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                        var role = doc.RootElement.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
                        var text = doc.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                        var row = "{\"role\":" + System.Text.Json.JsonSerializer.Serialize(role)
                            + ",\"text\":" + System.Text.Json.JsonSerializer.Serialize(text)
                            + ",\"artifacts\":" + a.GetRawText() + "}";
                        // 该行引用的 blob 文件哈希（首文件）→ 与 MAF 图片字节比对
                        string? sha = null;
                        if (a.TryGetProperty("files", out var fs) && fs.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var f in fs.EnumerateArray())
                            {
                                var path = f.TryGetProperty("filePath", out var fp) ? fp.GetString() : null;
                                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                                {
                                    using var s2 = File.OpenRead(path);
                                    sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s2));
                                    break;
                                }
                            }
                        }
                        if (sha is null) continue;   // 无可用文件：行无法匹配，丢弃（占位替代）
                        if (!artifactBySha.TryGetValue(sha, out var q)) artifactBySha[sha] = q = new Queue<string>();
                        q.Enqueue(row);
                    }
                    catch { }
                }
            }

            var sb = new System.Text.StringBuilder();
            foreach (var m in history)
            {
                if (m.Role == Microsoft.Extensions.AI.ChatRole.System) continue;   // 前情摘要不入转写
                // IM 图片消息：MAF 内容=DataContent 图片 + host 内部指令文本——不入转写（防指令泄露），
                // 原位用**字节哈希匹配**的附件行回填（图片渲染数据源）
                if (m.Role == Microsoft.Extensions.AI.ChatRole.User
                    && m.Contents.FirstOrDefault(c => c is Microsoft.Extensions.AI.DataContent dc
                        && (dc.MediaType ?? "").StartsWith("image/")) is Microsoft.Extensions.AI.DataContent img)
                {
                    var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(img.Data.Span));
                    if (artifactBySha.TryGetValue(sha, out var q) && q.Count > 0)
                        sb.Append(q.Dequeue()).Append('\n');
                    else
                        sb.Append("{\"role\":\"user\",\"text\":\"[附件]\"}\n");   // 旧会话无附件行/文件已清理
                    continue;
                }
                var text = (m.Text ?? "").Trim();
                if (text.Length == 0) continue;                                    // 工具轮无正文
                var role = m.Role == Microsoft.Extensions.AI.ChatRole.User ? "user" : "assistant";
                sb.Append("{\"role\":\"").Append(role).Append("\",\"text\":")
                  .Append(System.Text.Json.JsonSerializer.Serialize(text)).Append("}\n");
            }
            var desired = sb.ToString();
            var existing = File.Exists(transcriptPath) ? File.ReadAllText(transcriptPath) : "";
            if (existing == desired) return;   // 幂等：无差异不重写（避免无谓 mtime 变更）
            File.WriteAllText(transcriptPath, desired, new System.Text.UTF8Encoding(false));
            ImDiag.Log($"[im] transcript 净化：{Path.GetFileName(sessionDir)}（{existing.Length}→{desired.Length} 字符）");
        }
        catch (Exception ex)
        {
            ImDiag.Log($"[im] transcript 净化失败 {Path.GetFileName(sessionDir)}：{ex.Message}");
        }
    }

    /// <summary>存量 IM 会话补注册（旧二进制期间创建的不在 legacy index——UI 不可见）：
    /// routes 表驱动，标题从 inbox 最新 sender_name 拼（与调度器同形态）；快照镜像 CURRENT。
    /// Agent 未就绪（legacy 库 null）则周期重试直至成功或放弃（1 分钟）。</summary>
    private async Task BackfillLegacySessionsAsync()
    {
        var sessionsDir = Path.Combine(_workspaceRoot, "sessions");
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var legacy = LegacyStore();
            if (legacy is not null && BackfillOnce(legacy, sessionsDir)) return;
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        ImDiag.Log("[im] 存量会话补注册放弃（Agent 长时间未就绪）");
    }

    private bool BackfillOnce(AgentBrook.Agent.Infrastructure.SessionStore legacy, string sessionsDir)
    {
        Microsoft.Agents.AI.AIAgent? sanitizeAgent = null;
        try { sanitizeAgent = _agentHost?.GetOrCreateAgent(); }
        catch (Exception ex)
        {
            // 净化代理构建失败（模型配置缺失等）不致命：注册/镜像照常，净化本轮跳过
            ImDiag.Log($"[im] 净化代理构建失败（本轮跳过净化）：{ex.Message}");
        }
        return BackfillSessionsCore(_db!, _store!, legacy, sessionsDir, sanitizeAgent);
    }

    /// <summary>回填核心（internal 供测试）：routes 驱动 → legacy 注册 + CURRENT 快照镜像 + transcript 净化。返回是否成功。</summary>
    internal static bool BackfillSessionsCore(ImDatabase db, VersionedSessionStore store,
        AgentBrook.Agent.Infrastructure.SessionStore legacy, string sessionsDir,
        Microsoft.Agents.AI.AIAgent? sanitizeAgent = null)
    {
        try
        {
            var rows = new List<(string SessionId, string Title)>();
            db.Gate.Wait();
            try
            {
                using var cmd = db.CreateCommand("""
                    SELECT r.session_id, r.channel_id, r.principal_id,
                      (SELECT sender_name FROM inbox WHERE channel_id = r.channel_id AND chat_id = r.chat_id
                       ORDER BY rowid DESC LIMIT 1) AS name
                    FROM routes r
                    """);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var sid = reader.GetString(0);
                    var channel = reader.GetString(1);
                    var senderId = reader.GetString(2);
                    var name = reader.IsDBNull(3) ? null : reader.GetString(3);
                    rows.Add((sid, $"{name ?? senderId}［{channel}］"));
                }
            }
            finally
            {
                db.Gate.Release();
            }
            foreach (var (sid, title) in rows)
            {
                try
                {
                    if (legacy.Get(sid) is null) legacy.Create(sid, title);
                    byte[] payload;
                    try { payload = store.ReadCurrentPayload(sid); }
                    catch (Exception ex)
                    {
                        // 孤儿会话（删除残留：无 CURRENT/版本目录）——跳过，不得毒杀整轮回填
                        ImDiag.Log($"[im] 跳过孤儿会话 {sid}：{ex.Message}");
                        continue;
                    }
                    var dir = Path.Combine(sessionsDir, sid);
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, "session.json"), payload);
                    // 启动**不重写** transcript.jsonl（SanitizeImTranscript 已停用）：它按 MAF 图片字节哈希
                    // 回填附件行，而 MAF 快照的图片编码与 Blob 原文哈希不一定一致 → 匹配失败的图片被改写成
                    // "[附件]" 占位（重启后图片消失）。转写现由提交投影独占写（含 artifacts）；双写已修掉，
                    // 历史垃圾不值得用"重写"去清。
                }
                catch (Exception ex)
                {
                    ImDiag.Log($"[im] 会话回填失败（跳过）：{sid}：{ex.Message}");
                }
            }
            ImDiag.Log($"[im] 存量会话补注册完成：{rows.Count} 个");
            return true;
        }
        catch (Exception ex)
        {
            ImDiag.Log($"[im] 存量会话补注册失败（将重试）：{ex.Message}");
            return false;
        }
    }

    private static ImChannelCapabilities Caps() => new(
        4096, 45L * 1024 * 1024, 20L * 1024 * 1024, 10, true, 1024, "None",
        TimeSpan.FromMinutes(2), 18, HasOutboundIdempotencyKey: true);
}
