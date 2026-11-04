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
    private readonly List<(string ChannelId, string InstanceId)> _bound = [];
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

        // 3) 组合启动（幂等）+ 热绑定
        var result = await EnsureStartedAsync().ConfigureAwait(false);
        return result.StateJson.Contains("\"telegram\"")
            ? $"Telegram 已启用（{result.BoundChannels.Count} 通道运行）"
            : "Telegram 配置已保存；通道绑定状态见日志";
    }

    /// <summary>把 "im" 类别加载器注册进旧 PluginManager（安装分流：IM 插件经新宿主加载）。
    /// 不依赖 Instance——注册时只需 agent.PluginManager；loader 回调在安装触发时才解析 Instance。</summary>
    private static void RegisterImCategoryLoader()
    {
        var agent = s_agentAccessor?.Invoke();
        if (agent?.PluginManager is null) return;
        agent.PluginManager.RegisterCategoryLoader("im", async dir =>
        {
            var (factory, _) = PluginPackageLoader.LoadFactory(dir);
            Instance?._pluginHost?.RegisterFactory(factory);
            await Console.Error.WriteLineAsync($"[im] 插件已由 IM 宿主加载（category=im）：{dir}");
        });
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
                Console.Error.WriteLine($"[im] 主窗口回合结果已入队送达 {sessionId}（{answer.Length} 字符）");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[im] 主窗口结果投递失败 {sessionId}：{ex.Message}");
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
            Console.Error.WriteLine($"[im] IM 会话已删除（墓碑+路由解绑）：{sessionId}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[im] IM 会话删除失败 {sessionId}：{ex.Message}");
        }
    }

    /// <summary>插件宿主（静态类别加载器用；未启动为 null）。</summary>
    internal PluginHost? PluginHostInternal => _pluginHost;
    internal MessagingService? MessagingInternal => _messaging;
    internal List<(string ChannelId, string InstanceId)> BoundInternal => _bound;

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
        Console.Error.WriteLine($"[im-compose] ImEnabled={settings.ImEnabled}, ws={s_workspaceRoot}");
        if (!settings.ImEnabled)
        {
            // U01：默认关闭 = 零副作用。不创建 im/ 目录、不注册路由、不启动恢复/通道、不迁移会话。
            Console.Error.WriteLine("[im-compose] 返回 disabled（零副作用）");
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
        BindErrors.Clear();
        LastTurnError = null;
        Console.Error.WriteLine("[im] IM 构建标记：R4-20261104（同权自动批准版）——若未见此行说明跑的是旧构建");
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
        Console.Error.WriteLine($"[im] recovery: {report.Actions.Count} actions, recoveryRequired={report.RecoveryRequiredReasons.Count}");

        // 通道插件包加载（主应用不静态引用插件实现）。
        // 包来源统一：直接扫 PluginManager 安装目录（workspace/plugins/{id}/ 最新叶子）——不依赖 Agent
        // 就绪时机（组合开机启动早于 Agent 就绪，ListInstalled 会拿到 null）；im/plugins/{id} 手工布点回退。
        _pluginHost = new PluginHost(new HostDatabase(imDir), imDir);
        _disposables.Add(_pluginHost);
        foreach (var (packageId, capabilityId) in new[] { ("im-telegram", "telegram-channel"), ("im-bridge", "bridge-channel") })
        {
            var dir = ResolveChannelPackageDir(packageId);
            if (dir is null) continue;
            try
            {
                var (factory, _) = PluginPackageLoader.LoadFactory(dir);
                _pluginHost.RegisterFactory(factory);
            }
            catch (Exception ex)
            {
                BindErrors.Add($"包加载 {packageId}：{ex.Message}");
                Console.Error.WriteLine($"[im] 包加载失败 {packageId}：{ex.Message}");
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
                Console.Error.WriteLine($"[im] 通道 {ch.ChannelId} 启动失败：{ex.Message}");
            }
        }

        var state = JsonSerializer.Serialize(new
        {
            enabled = true,
            channels = _bound.Select(b => new { b.ChannelId, b.InstanceId }),
            recoveryRequired = report.RecoveryRequiredReasons.Count,
        });
        return new ImCompositionResult(true, state, _gateway, _messaging, _bound.ToList());
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
        if (!string.IsNullOrWhiteSpace(ch.BridgeUrl) && Uri.TryCreate(ch.BridgeUrl, UriKind.Absolute, out var u))
            hosts.Add(u.Host);
        return hosts;
    }

    private async Task BindChannelAsync(ImChannelConfig ch)
    {
        if (_messaging is null || _pluginHost is null) return;
        var capabilityId = ch.Type switch
        {
            "telegram" => "telegram-channel",
            "bridge" => "bridge-channel",
            _ => throw new InvalidOperationException($"未知通道类型：{ch.Type}"),
        };
        var record = await _pluginHost.StartInstanceAsync(capabilityId, ch.InstanceId, BuildOutboundHosts(ch),
            CancellationToken.None,
            webhook: new HelperWebhookService(_gateway),
            blob: new HelperBlobService(_workspaceRoot)).ConfigureAwait(false);
        var channel = (IImChannel)record.Capability!;
        var sink = _messaging.BindChannel(ch.ChannelId, ch.InstanceId, accountEpoch: 1, channel,
            autoPairPrivateChats: true);   // Helper 形态：私聊首条即配对（防无配对 UI 死局）
        await channel.StartAsync(new ImChannelStartContext(sink, JsonSerializer.Serialize(new
        {
            channelId = ch.ChannelId,
            displayName = ch.DisplayName,
            // Telegram 平台基地址（插件设计为必填、不内置真实域名）；bridge 通道不使用
            apiBase = ch.Type == "telegram" ? "https://api.telegram.org" : "",
            bridgeUrl = ch.BridgeUrl ?? "",
            secretRef = ch.SecretRef,
            pollTimeoutSeconds = 30,
        })), CancellationToken.None).ConfigureAwait(false);
        _bound.Add((ch.ChannelId, ch.InstanceId));
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
                Console.Error.WriteLine("[im] 版本化持久化已附加到 BrookAgent");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        Console.Error.WriteLine("[im] 持久化附加放弃（Agent 长时间未就绪——运行时重载/版本化保存将不可用）");
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
            Console.Error.WriteLine($"[im] transcript 净化：{Path.GetFileName(sessionDir)}（{existing.Length}→{desired.Length} 字符）");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[im] transcript 净化失败 {Path.GetFileName(sessionDir)}：{ex.Message}");
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
        Console.Error.WriteLine("[im] 存量会话补注册放弃（Agent 长时间未就绪）");
    }

    private bool BackfillOnce(AgentBrook.Agent.Infrastructure.SessionStore legacy, string sessionsDir)
    {
        Microsoft.Agents.AI.AIAgent? sanitizeAgent = null;
        try { sanitizeAgent = _agentHost?.GetOrCreateAgent(); }
        catch (Exception ex)
        {
            // 净化代理构建失败（模型配置缺失等）不致命：注册/镜像照常，净化本轮跳过
            Console.Error.WriteLine($"[im] 净化代理构建失败（本轮跳过净化）：{ex.Message}");
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
                        Console.Error.WriteLine($"[im] 跳过孤儿会话 {sid}：{ex.Message}");
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
                    Console.Error.WriteLine($"[im] 会话回填失败（跳过）：{sid}：{ex.Message}");
                }
            }
            Console.Error.WriteLine($"[im] 存量会话补注册完成：{rows.Count} 个");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[im] 存量会话补注册失败（将重试）：{ex.Message}");
            return false;
        }
    }

    private static ImChannelCapabilities Caps() => new(
        4096, 45L * 1024 * 1024, 20L * 1024 * 1024, 10, true, 1024, "None",
        TimeSpan.FromMinutes(2), 18, HasOutboundIdempotencyKey: true);
}
