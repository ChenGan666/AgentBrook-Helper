using AgentBrook.Messaging.Abstractions;
using AgentBrook.Runtime;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Messaging;

/// <summary>
/// IM 领域服务组合（设计 §3/§15）：准入 Sink + 唤醒循环 + 出站调度 + 控制通道。
/// 恢复顺序（启动对账）：领域恢复（取消意图优先）→ Inbox 补扫 → Runtime 恢复对账 → 正常泵。
/// </summary>
public sealed class MessagingService : IAsyncDisposable
{
    private readonly ImDatabase _db;
    private readonly SessionFileSystem _fs;
    private readonly CommitJournal _journal;
    private readonly DeleteProtocol _delete;
    private readonly RecoveryService _runtimeRecovery;
    private readonly MessagingStore _store;
    private readonly IdentitiesStore _identities;
    private readonly ExecutionQuotas _quotas = new();
    private readonly CancellationTokenSource _cts = new();

    public MessagingStore Store => _store;
    public IdentitiesStore Identities => _identities;
    public ExecutionQuotas Quotas => _quotas;
    public ISessionFactory Sessions { get; }
    public Func<TurnExecutionContext, ITurnExecutor> ExecutorFactory { get; }
    public IApprovalPort Approvals { get; }
    public ImChannelCapabilities Capabilities { get; }
    public DisclosurePolicy Disclosure { get; } = new();

    private readonly Dictionary<string, IImChannel> _channels = [];
    private readonly Dictionary<string, ImEndpointIdentity> _identitiesByNs = [];
    private readonly Dictionary<string, IImInboundSink> _sinks = [];

    public TurnDispatcher Dispatcher { get; }
    public OutboxDispatcher Outbox { get; }
    public ControlCommands Control { get; }
    private readonly List<Task> _loops = [];

    public MessagingService(
        ImDatabase db,
        ISessionFactory sessions,
        Func<TurnExecutionContext, ITurnExecutor> executorFactory,
        IApprovalPort approvals,
        ImChannelCapabilities capabilities,
        string? workspaceRootOverride = null)
    {
        _db = db;
        // 会话文件根：默认=db 所在目录（S2～S6 测试形态：root/im.db + root/sessions）；
        // Helper 组合传 workspaceRoot（root/im/im.db + root/sessions——设计 §12 布局）
        _fs = new SessionFileSystem(workspaceRootOverride ?? db.RootPath);
        _journal = new CommitJournal(db, _fs);
        _delete = new DeleteProtocol(db, _fs, _journal);
        _runtimeRecovery = new RecoveryService(db, _fs, _journal, _delete);
        _store = new MessagingStore(db);
        _identities = new IdentitiesStore(db);
        _store.EnsureSchema();
        Sessions = sessions;
        ExecutorFactory = executorFactory;
        Approvals = approvals;
        Capabilities = capabilities;

        Dispatcher = new TurnDispatcher(_store, new SessionExecutionCoordinator(_fs), _journal,
            sessions, executorFactory, approvals, Disclosure, Capabilities, _quotas, _cts.Token);
        Outbox = new OutboxDispatcher(_store, ResolveChannel, ResolveIdentity, new OutboxOptions(), _cts.Token);
        Control = new ControlCommands(_store, new SessionExecutionCoordinator(_fs), _identities);
    }

    // ── 通道注册（实例绑定 Sink：宿主按实例创建句柄，§4.1）──

    public IImInboundSink BindChannel(string channelId, string instanceId, long accountEpoch, IImChannel channel,
        bool autoPairPrivateChats = false)
    {
        var identity = new ImEndpointIdentity(channelId, instanceId, accountEpoch);
        var sink = new InboundPipeline(_store, _identities, identity, new InboundQuotas(MaxPerMinute: 100000),
            onAccepted: _ => Dispatcher.Wake(), autoPairPrivateChats: autoPairPrivateChats);
        _channels[$"{channelId}+{instanceId}"] = channel;
        _identitiesByNs[$"{channelId}+{instanceId}"] = identity;
        _sinks[$"{channelId}+{instanceId}"] = sink;
        return sink;
    }

    public IImInboundSink? GetSink(string channelId, string instanceId) =>
        _sinks.GetValueOrDefault($"{channelId}+{instanceId}");

    /// <summary>通道实例（附件物化 §8.1：统一管道下由通道插件实现下载/格式处理）。</summary>
    public IImChannel? GetChannel(string channelId, string instanceId) =>
        _channels.GetValueOrDefault($"{channelId}+{instanceId}");

    /// <summary>
    /// 删除 IM 会话（删除协议全量：墓碑落库 + 版本目录清理 + 路由解绑）。
    /// UI 删除 IM 会话必须走这里——只删 legacy 目录会让路由悬空：墓碑缺失时同 chat 新消息
    /// 会按"路由丢失"复活旧 id（且工厂幂等跳过种子化 → 回合失败无回复）。
    /// </summary>
    public async Task DeleteSessionAsync(string sessionId, Func<Task>? waitForInFlightAsync = null)
    {
        await _delete.DeleteSessionAsync(sessionId, waitForInFlightAsync).ConfigureAwait(false);
        _store.DeleteRoute(sessionId);
    }

    /// <summary>
    /// 主窗口 IM 会话发起的回复入队（双向闭环：用户在主界面 IM 会话中输入 → 结果送达该 IM）。
    /// 路由缺失（非 IM 会话）或通道未绑定（避免永久 Pending）返回 false；文本走同一披露分片策略。
    /// </summary>
    public bool EnqueueOutbound(string sessionId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var route = _store.GetRoute(sessionId);
        if (route is null) return false;
        var ns = $"{route.ChannelId}+{route.InstanceId}";
        if (!_sinks.ContainsKey(ns)) return false;
        var fragments = Disclosure.PlanOutbound(route.PrincipalId, text, Capabilities);
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var fragment in fragments)
        {
            var deliveryId = $"ob-{Guid.NewGuid():N}"[0..16];
            _store.InsertOutbox(new OutboxRow(
                deliveryId, ns, sessionId, $"local-{Guid.NewGuid():N}"[0..14],
                1, route.AccountId, route.ChatId, fragment, null, $"idem-{deliveryId}", null,
                "Pending", 0, null, false, null, null, now, now, now));
        }
        return fragments.Count > 0;
    }

    private IImChannel? ResolveChannel(string ns) => _channels.GetValueOrDefault(ns);

    private ImEndpointIdentity? ResolveIdentity(string ns) => _identitiesByNs.GetValueOrDefault(ns);

    // ── 生命周期 ──

    /// <summary>启动：恢复（取消优先）→ 泵循环。</summary>
    public async Task StartAsync()
    {
        // ① 领域恢复：取消意图优先（v4.3 恢复顺序），再查询回填
        await Outbox.RecoverAsync().ConfigureAwait(false);

        // ② Inbox 补扫：Processing → Pending（F02 唤醒丢失恢复）
        _store.RescheduleInterruptedInbox();

        // ③ Runtime 恢复对账（删除优先→补提交→Running 矩阵）
        await _runtimeRecovery.RecoverAsync().ConfigureAwait(false);

        // ④ 泵循环
        _loops.Add(Task.Run(() => Dispatcher.RunAsync()));
        _loops.Add(Task.Run(DeliveryLoopAsync));
        Dispatcher.Wake();
    }

    private async Task DeliveryLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Outbox.PumpAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* 单轮失败不终止服务（下轮重试） */ }
            try { await Task.Delay(200, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>停止：停通道收发 → 停泵 → 保留持久任务状态。</summary>
    public async Task StopAsync(TimeSpan gracePeriod)
    {
        foreach (var channel in _channels.Values)
            await channel.StopAsync(gracePeriod).ConfigureAwait(false);
        _cts.Cancel();
        try { await Task.WhenAll(_loops).ConfigureAwait(false); }
        catch { /* 循环取消/单轮异常已隔离 */ }
        _quotas.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _quotas.Dispose();
        _cts.Dispose();
        await _db.DisposeAsync().ConfigureAwait(false);
    }
}
