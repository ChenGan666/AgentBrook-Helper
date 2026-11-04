using AgentBrook.Plugin.Abstractions;
using AgentBrook.Plugin.Hosting.Abstractions;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Plugin.Hosting;

/// <summary>实例运行信息（宿主登记）。</summary>
public sealed class PluginInstanceRecord
{
    public required InstanceRef Instance { get; init; }
    public ICapabilityInstance? Capability { get; set; }
    public required ActivationId Activation { get; init; }
    public PluginRunState State { get; set; } = PluginRunState.Loaded;
    public string? LastError { get; set; }
    public required InstanceServices Services { get; init; }
}

/// <summary>实例级已授予服务集合（§15.2 双层授权：授予层）。Get 按类型查；未授予即 null（默认拒绝）。</summary>
public sealed class InstanceServices
{
    private readonly Dictionary<Type, object> _services = new();

    public void Grant<T>(T service) where T : class => _services[typeof(T)] = service;

    public T? Get<T>() where T : class => _services.TryGetValue(typeof(T), out var s) ? (T)s : null;

    /// <summary>通道插件默认授予表（§15.2）：实例绑定 Sink 之外的裸会话提交**不在其中**（双层授权堵第二入口）。
    /// webhook/blob 可选授予（桥通道需要；S6 端到端与宿主组合提供）。</summary>
    public static InstanceServices ForChannelPlugin(
        HostDatabase db, ActivationRegistry activations, InstanceRef instance, ActivationId activation,
        string rootPath, IReadOnlyList<string> outboundHosts, AuditLog audit,
        IWebhookService? webhook = null, IBlobService? blob = null)
    {
        var services = new InstanceServices();
        var ns = $"{instance.PluginId}+{instance.InstanceId}";
        var credentials = new CredentialService(db, ns, activation, activations);
        services.Grant<IPluginStateStore>(new NamespaceStateStore(db, activations, ns, activation));
        services.Grant<ICredentialService>(credentials);
        services.Grant<INetworkService>(new NetworkService(outboundHosts, credentials, activations, activation));
        services.Grant<IBackgroundTaskService>(new BackgroundTaskService(activations, activation, m => audit.Write("plugin", "bg-fault", new Dictionary<string, string?> { ["msg"] = m })));
        if (webhook is not null) services.Grant<IWebhookService>(webhook);
        if (blob is not null) services.Grant<IBlobService>(blob);
        audit.FieldRedactor = value => credentials.Redact(value);
        return services;
    }
}

/// <summary>
/// 通用插件宿主（设计 §15.2/§15.4）：能力工厂注册（插件级可撤销租约）、
/// 实例生命周期（Loaded→Starting→Active→Stopping→Disabled/Failed）、活动租约签发与撤销。
/// 同时实现 IPluginHostExtension（旧 IHelperPlugin 插件的扩展协商入口，C02）。
/// </summary>
public sealed class PluginHost(HostDatabase db, string rootPath) : IPluginHostExtension, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ActivationRegistry _activations = new();
    private readonly Dictionary<string, (ICapabilityFactory Factory, RegistrationLease Lease)> _factories = [];
    private readonly Dictionary<string, PluginInstanceRecord> _instances = [];
    private readonly AuditLog _audit = new(Path.Combine(rootPath, "audit.jsonl"));

    public AuditLog Audit => _audit;

    /// <summary>插件日志镜像出口（宿主注入：让插件日志与宿主日志落到同一诊断文件，用户一处可见）。</summary>
    public static Action<string>? DiagnosticsSink { get; set; }
    public ActivationRegistry Activations => _activations;

    /// <summary>共享契约清单（§15.4：宿主控制、加载前版本检查；不整体共享 AgentBrook.* 内部实现）。</summary>
    public static IReadOnlyList<string> SharedContractAssemblies { get; } =
    [
        "AgentBrook.Plugin.Abstractions",
        "AgentBrook.Runtime.Abstractions",
        "AgentBrook.Plugin.Hosting.Abstractions",
        "AgentBrook.Messaging.Abstractions",
    ];

    // ── 能力工厂注册（插件级）──

    public IRegistrationLease RegisterFactory(ICapabilityFactory factory)
    {
        if (!HostingContract.IsCompatible(HostingContract.Version, factory.MinContractVersion))
            throw new HostPipelineException(ImErrorCodes.ContractVersionMismatch,
                $"工厂 {factory.CapabilityId} 要求 {factory.MinContractVersion}，宿主 {HostingContract.Version}");
        var lease = new RegistrationLease();
        lock (_gate) _factories[factory.CapabilityId] = (factory, lease);
        return lease;
    }

    /// <summary>
    /// 卸载某插件的全部能力（卸载前调用）：先停其所有实例，再注销工厂并撤销注册租约。
    /// 程序集回收由 <see cref="PluginPackageLoadContext"/>（collectible）在引用释放后完成——
    /// 调用方应随后 GC 并重试删除目录（文件锁在 ALC 卸下后才释放）。
    /// </summary>
    public async Task<int> UnloadPluginAsync(string pluginId, TimeSpan gracePeriod)
    {
        List<string> instanceIds;
        int removed = 0;
        lock (_gate)
        {
            instanceIds = [.. _instances
                .Where(kv => kv.Value.Instance.PluginId == pluginId)
                .Select(kv => kv.Key)];
        }
        foreach (var instanceId in instanceIds)
        {
            try { await StopInstanceAsync(instanceId, gracePeriod).ConfigureAwait(false); }
            catch (Exception) { /* 停实例失败不阻断工厂注销（卸载尽力而为） */ }
        }
        lock (_gate)
        {
            foreach (var key in _factories
                .Where(kv => kv.Value.Factory.PluginId == pluginId)
                .Select(kv => kv.Key).ToList())
            {
                _factories[key].Lease.Revoke();
                _factories.Remove(key);
                removed++;
            }
        }
        return removed;
    }

    // ── 实例生命周期 ──

    /// <summary>创建并启动实例：签发活动租约 → 授予服务 → 工厂创建 → Starting/Active。</summary>
    public async Task<PluginInstanceRecord> StartInstanceAsync(
        string capabilityId, string instanceId, IReadOnlyList<string> outboundHosts,
        CancellationToken cancellationToken = default,
        IWebhookService? webhook = null, IBlobService? blob = null)
    {
        lock (_gate)
        {
            if (!_factories.TryGetValue(capabilityId, out var entry) || entry.Lease.IsRevoked)
                throw new HostPipelineException(ImErrorCodes.ServiceNotGranted, $"能力工厂未注册或已撤销：{capabilityId}");
        }
        var (factory, _) = _factories[capabilityId];

        var instance = new InstanceRef(factory.PluginId, capabilityId, instanceId);
        var activation = _activations.Issue();
        var services = InstanceServices.ForChannelPlugin(db, _activations, instance, activation, rootPath, outboundHosts, _audit, webhook, blob);

        var record = new PluginInstanceRecord
        {
            Instance = instance,
            Activation = activation,
            Services = services,
            State = PluginRunState.Starting,
        };

        ICapabilityInstance capability;
        try
        {
            var context = new ExtensionContextImpl(instance, activation, services, HostingContract.Version,
                () => _activations.IsAlive(activation), _audit);
            capability = await factory.CreateAsync(new InstanceCreationContext(instance, activation, context), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _activations.Revoke(activation);
            record.State = PluginRunState.Failed;
            record.LastError = ex.Message;
            throw;
        }

        record.Capability = capability;
        record.State = PluginRunState.Active;
        // 键 = {capabilityId}+{instanceId}：各通道实例 id 都叫 "inst-1"，只按 instanceId 索引会互相覆盖，
        // 导致"停错实例、旧轮询存活 → 持续报错/抢同一游标"
        lock (_gate) _instances[InstanceKey(instance.CapabilityId, instanceId)] = record;
        return record;
    }

    /// <summary>
    /// 停用实例（§15.4 停用语义）：撤销活动代次（旧句柄全部失效）→ 取消后台任务 →
    /// 协作等待在途（超时 → Failed）→ 注销。停用一个实例不影响其他实例（工厂与租约是插件级/实例级分离）。
    /// </summary>
    private static string InstanceKey(string capabilityId, string instanceId) => $"{capabilityId}+{instanceId}";

    /// <summary>按（能力, 实例）停止：多通道共用 "inst-1" 时必须带能力 id，否则会停错实例。</summary>
    public async Task<bool> StopInstanceAsync(string capabilityId, string instanceId, TimeSpan gracePeriod)
    {
        PluginInstanceRecord? record;
        lock (_gate)
        {
            if (!_instances.TryGetValue(InstanceKey(capabilityId, instanceId), out record)) return false;
            _instances.Remove(InstanceKey(capabilityId, instanceId));
        }
        return await StopRecordAsync(record, gracePeriod).ConfigureAwait(false);
    }

    /// <summary>兼容重载：仅按 instanceId 停止（命中第一个匹配；新代码请用带 capabilityId 的重载）。</summary>
    public Task<bool> StopInstanceAsync(string instanceId, TimeSpan gracePeriod)
    {
        PluginInstanceRecord? record;
        lock (_gate)
        {
            var matched = _instances.FirstOrDefault(kv => kv.Value.Instance.InstanceId == instanceId);
            if (matched.Value is null) return Task.FromResult(false);
            _instances.Remove(matched.Key);
            record = matched.Value;
        }
        return StopRecordAsync(record, gracePeriod);
    }

    private async Task<bool> StopRecordAsync(PluginInstanceRecord record, TimeSpan gracePeriod)
    {
        record.State = PluginRunState.Stopping;

        // ① 撤销活动代次：此后旧句柄的所有服务调用立即拒绝（H02）
        _activations.Revoke(record.Activation);

        // ② 取消受管后台任务并等待
        var background = record.Services.Get<IBackgroundTaskService>() as BackgroundTaskService;
        var stoppedCleanly = true;
        if (background is not null)
            stoppedCleanly = await background.StopAllAsync(gracePeriod).ConfigureAwait(false);

        // ③ 协作停止实例
        if (record.Capability is null)
        {
            record.State = PluginRunState.Failed;
            record.LastError = "实例能力未创建（内部状态错误）。";
            return false;
        }
        try
        {
            await record.Capability.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            record.LastError = ex.Message;
            stoppedCleanly = false;
        }

        record.State = stoppedCleanly ? PluginRunState.Disabled : PluginRunState.Failed;
        if (!stoppedCleanly) record.LastError ??= "停止超时：在途任务未在宽限期内退出。";
        return stoppedCleanly;
    }

    public PluginInstanceRecord? GetInstance(string instanceId) =>
        ListInstances().FirstOrDefault(r => r.Instance.InstanceId == instanceId);

    public PluginInstanceRecord? GetInstance(string capabilityId, string instanceId)
    {
        lock (_gate) return _instances.GetValueOrDefault(InstanceKey(capabilityId, instanceId));
    }

    public IReadOnlyList<PluginInstanceRecord> ListInstances()
    {
        lock (_gate) return [.. _instances.Values];
    }

    // ── 旧契约协商入口（IPluginHostExtension）──

    public Task<IExtensionContext?> NegotiateAsync(ExtensionRequest request, CancellationToken cancellationToken)
    {
        if (!HostingContract.IsCompatible(HostingContract.Version, request.MinContractVersion))
            return Task.FromResult<IExtensionContext?>(null);
        var instance = new InstanceRef(request.PluginId, request.PluginId, request.PluginId);
        var activation = _activations.Issue();
        var services = InstanceServices.ForChannelPlugin(db, _activations, instance, activation, rootPath, [], _audit);
        IExtensionContext context = new ExtensionContextImpl(instance, activation, services, HostingContract.Version,
            () => _activations.IsAlive(activation), _audit);
        return Task.FromResult<IExtensionContext?>(context);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var record in ListInstances())
            await StopInstanceAsync(record.Instance.InstanceId, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await db.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class RegistrationLease : IRegistrationLease
    {
        public bool IsRevoked { get; private set; }
        public event Action? Revoked;

        /// <summary>显式撤销（卸载插件时注销工厂用；与 Dispose 同语义）。</summary>
        public void Revoke() => Dispose();

        public void Dispose()
        {
            if (IsRevoked) return;
            IsRevoked = true;
            Revoked?.Invoke();
        }
    }
}

/// <summary>
/// 扩展上下文实现：按权限返回已授予服务；活动租约失效后 <b>所有</b> GetService 返回 null
/// （H02：停用后旧句柄新调用通过数=0），不再提供任何能力。
/// </summary>
public sealed class ExtensionContextImpl(
    InstanceRef instance,
    ActivationId activation,
    InstanceServices services,
    string contractVersion,
    Func<bool> isAlive,
    AuditLog audit) : IExtensionContext
{
    public string ContractVersion { get; } = contractVersion;
    public InstanceRef Instance { get; } = instance;
    public ActivationId Activation { get; } = activation;

    public T? GetService<T>() where T : class
    {
        if (!isAlive()) return null;   // 旧活动实例：拒绝一切新调用（静默失效语义：拿不到服务）
        return services.Get<T>();
    }

    public void Log(string message)
    {
        audit.Write("plugin", "log", new Dictionary<string, string?>
        {
            ["instance"] = $"{Instance.PluginId}/{Instance.InstanceId}",
            ["message"] = message,
        });
        try { PluginHost.DiagnosticsSink?.Invoke($"[{Instance.PluginId}] {message}"); } catch { /* 诊断出口失败不影响插件 */ }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// 旧 IHelperPlugin 适配（旁路新增，不改旧契约 §3.1）：在新宿主上以扩展协商装载旧契约插件；
/// 协商失败 → 明确拒绝（版本要求），不偷偷降级。
/// </summary>
public static class LegacyPluginAdapter
{
    public static async Task InitializeAsync(IHelperPlugin legacyPlugin, IExtensionContext context, CancellationToken ct = default)
    {
        var host = new LegacyHostView(context);
        await legacyPlugin.InitializeAsync(host, ct).ConfigureAwait(false);
    }

    /// <summary>把新扩展上下文呈现为旧 IPluginHostContext（只映射旧契约成员）+ 协商入口（已协商上下文原样返回）。</summary>
    private sealed class LegacyHostView(IExtensionContext context) : IPluginHostContext, IPluginHostExtension
    {
        public string WorkspaceRoot { get; } = "";   // 旧插件工作区根：新宿主不暴露宿主绝对路径（§15.2）
        public string Language => "zh";
        public IPluginSettingsStore Settings => throw new NotSupportedException("旧设置存储经 state-store 命名空间迁移（S7 接线）。");
        public IPluginDownloadService Download => throw new NotSupportedException("统一下载服务经受控网络服务提供（S7 接线）。");
        public void Log(string message) => context.Log(message);
        public void RequestPipelineRebuild() { }

        public Task<IExtensionContext?> NegotiateAsync(ExtensionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<IExtensionContext?>(context);   // 已协商成功，原上下文返回（组合顺序不变）
    }
}
