using AgentBrook.Messaging.Abstractions;
using AgentBrook.Runtime;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Messaging;

/// <summary>
/// 出站投递调度器（设计 §10.1）：Outbox → 通道 SubmitDeliveryAsync → 状态机迁移。
/// Unknown 语义：HasOutboundIdempotencyKey=true 可自动重试；=false 不自动重发（仅人工）。
/// AcceptedByProvider ≠ Sent；查询/取消经公共可选能力接口，不拼通道专有 HTTP。
/// </summary>
public sealed class OutboxDispatcher(
    MessagingStore store,
    Func<string, IImChannel?> channelResolver,
    Func<string, ImEndpointIdentity?> identityResolver,
    OutboxOptions options,
    CancellationToken serviceCancellationToken,
    Action<string>? log = null)
{
    /// <summary>按 DeliveryId 解析已注册投递能力（查询/取消）。</summary>
    public IDeliveryStatusProvider? StatusProviderResolver(string ns) => channelResolver(ns) as IDeliveryStatusProvider;
    public IDeliveryCancellationProvider? CancellationProviderResolver(string ns) => channelResolver(ns) as IDeliveryCancellationProvider;

    /// <summary>投递扫描一轮（宿主定时器/循环驱动；DB 触碰持连接门，通道网络调用在 DeliverAsync 内亦受门保护——脚本通道瞬时，S6 平台化时改细粒度）。</summary>
    public async Task PumpAsync()
    {
        await store.Db.Gate.WaitAsync(serviceCancellationToken).ConfigureAwait(false);
        try
        {
            await PumpCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            store.Db.Gate.Release();
        }
    }

    private async Task PumpCoreAsync()
    {
        await SweepQueriesAsync().ConfigureAwait(false);

        foreach (var row in store.ListOutbox(OutboxState.Pending.ToString()))
        {
            if (serviceCancellationToken.IsCancellationRequested) return;
            if (row.CancelRequested)
            {
                await DispatchCancelAsync(row).ConfigureAwait(false);
                continue;
            }
            if (row.NextRetryAt is { } retry && DateTimeOffset.Parse(retry) > DateTimeOffset.UtcNow) continue;
            await DeliverAsync(row).ConfigureAwait(false);
        }

        // FailedTemporary 到期回 Pending（有界自动重试）
        foreach (var row in store.ListOutbox(OutboxState.FailedTemporary.ToString()))
        {
            if (row.NextRetryAt is { } at && DateTimeOffset.Parse(at) <= DateTimeOffset.UtcNow)
                store.TransitionOutbox(row.DeliveryId, OutboxState.Pending);
        }
    }

    /// <summary>单条投递。返回最终状态（测试断言用）。</summary>
    public async Task<OutboxState> DeliverAsync(OutboxRow row)
    {
        var channel = channelResolver(row.Namespace);
        if (channel is null)
        {
            // 插件不可用：保留状态等待（§15.4 任务保留策略——不等同撤权或删除）
            log?.Invoke($"通道不可用，保留待发：{row.DeliveryId}");
            return Enum.Parse<OutboxState>(row.Status);
        }

        // 发送前核对目标账号代次（§11.6 换号保护）
        var identity = identityResolver(row.Namespace);
        if (identity is { } bound && bound.AccountEpoch != row.AccountEpoch)
        {
            store.TransitionOutbox(row.DeliveryId, OutboxState.Cancelled, error: ImErrorCodes.AccountMismatch);
            return OutboxState.Cancelled;
        }

        var files = row.FilesJson is null ? null : ImJson.Deserialize<List<ContentRef>>(row.FilesJson);
        var request = new DeliveryRequest(
            row.DeliveryId, row.AccountEpoch, row.AccountId, row.ChatId,
            row.Text, files, row.IdempotencyKey,
            row.Deadline is null ? null : DateTimeOffset.Parse(row.Deadline));

        store.IncrementAttempts(row.DeliveryId);

        DeliveryResult result;
        try
        {
            result = await channel.SubmitDeliveryAsync(request, serviceCancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Enum.Parse<OutboxState>(row.Status);   // 服务停止：保留现场
        }
        catch (Exception ex)
        {
            // 异常 = 传输失败：无幂等键通道视为 Unknown（结果未知不自动重发）；有幂等键临时失败可重试
            result = channel.Capabilities.HasOutboundIdempotencyKey
                ? DeliveryResult.Rejected(ImErrorCodes.Temporary, ex.Message)
                : DeliveryResult.Unknown(ex.Message);
        }

        switch (result.State)
        {
            case DeliveryState.Accepted:
                store.TransitionOutbox(row.DeliveryId, OutboxState.AcceptedByProvider);
                break;
            case DeliveryState.PlatformAccepted:
                store.TransitionOutbox(row.DeliveryId, OutboxState.Sent, platformMessageId: result.PlatformMessageId);
                break;
            case DeliveryState.Rejected when result.ErrorCategory == ImErrorCodes.Temporary:
                store.TransitionOutbox(row.DeliveryId, OutboxState.FailedTemporary,
                    error: result.Message, retryAfter: result.RetryAfter ?? options.TemporaryRetryBackoff);
                break;
            case DeliveryState.Rejected when result.ErrorCategory == ImErrorCodes.Permanent:
                store.TransitionOutbox(row.DeliveryId, OutboxState.FailedPermanent, error: result.Message);
                break;
            case DeliveryState.Rejected when result.ErrorCategory == "Cancelled":
                // 桥取消墓碑拦截后到的 /send（契约 v1.5）：宿主侧记 Cancelled（B05）
                store.TransitionOutbox(row.DeliveryId, OutboxState.Cancelled, error: result.Message);
                break;
            case DeliveryState.Rejected:
                store.TransitionOutbox(row.DeliveryId, OutboxState.FailedTemporary,
                    error: result.Message, retryAfter: result.RetryAfter ?? options.TemporaryRetryBackoff);
                break;
            case DeliveryState.Unknown:
            default:
                // 429（RetryAfter 明确未发送）可自动重试；其余 Unknown 不自动重发（M08/T05）
                if (result.RetryAfter is { } wait)
                {
                    store.TransitionOutbox(row.DeliveryId, OutboxState.FailedTemporary,
                        error: result.Message, retryAfter: wait);
                }
                else
                {
                    store.TransitionOutbox(row.DeliveryId, OutboxState.Unknown, error: result.Message);
                }
                break;
        }

        return Enum.Parse<OutboxState>(store.GetOutbox(row.DeliveryId)!.Status);
    }

    /// <summary>取消已提交且无确定终态的投递（v4.3）。意图已先持久化；三态回填，不谎报。</summary>
    public async Task<DeliveryCancelState> DispatchCancelAsync(OutboxRow row)
    {
        var current = Enum.Parse<OutboxState>(store.GetOutbox(row.DeliveryId)!.Status);
        if (OutboxStateMachine.IsTerminal(current)) return DeliveryCancelState.Cancelled;

        var cancellation = CancellationProviderResolver(row.Namespace);
        if (cancellation is null)
        {
            // 通道不支持取消：取消待确认（保留意图；UI 如实展示）
            log?.Invoke($"通道不支持取消，保留取消意图：{row.DeliveryId}");
            return DeliveryCancelState.Unknown;
        }

        DeliveryCancelState state;
        try
        {
            state = await cancellation.CancelAsync(row.DeliveryId, serviceCancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            state = DeliveryCancelState.Unknown;   // 受理方离线/响应丢失 → 取消待确认
        }

        switch (state)
        {
            case DeliveryCancelState.Cancelled:
                store.TransitionOutbox(row.DeliveryId, OutboxState.Cancelled);
                break;
            case DeliveryCancelState.AlreadyDispatched:
                // 在途消息如实展示：转 Unknown 等查询终态（不谎报 Cancelled）
                store.TransitionOutbox(row.DeliveryId, OutboxState.Unknown, error: "cancel: already dispatched");
                break;
            case DeliveryCancelState.Unknown:
            default:
                log?.Invoke($"取消待确认：{row.DeliveryId}（恢复后先补发取消/查询）");
                break;
        }
        return state;
    }

    /// <summary>
    /// 恢复顺序（v4.3）：先恢复取消意图（补发取消/查询回填），再恢复普通投递。
    /// </summary>
    public async Task RecoverAsync()
    {
        await store.Db.Gate.WaitAsync(serviceCancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            store.Db.Gate.Release();
        }
    }

    private async Task RecoverCoreAsync()
    {
        // ① 取消意图优先
        foreach (var row in store.ListOutbox().Where(r => r.CancelRequested && !OutboxStateMachine.IsTerminal(Enum.Parse<OutboxState>(r.Status))))
            await DispatchCancelAsync(row).ConfigureAwait(false);

        // ② 查询回填（泵循环每轮亦调用——AcceptedByProvider/Unknown 无终态行的状态追踪）
        await SweepQueriesAsync().ConfigureAwait(false);
    }

    /// <summary>查询回填：AcceptedByProvider/Unknown 行经公共状态接口查询终态（不回退 Pending、不触发重发）。</summary>
    private async Task SweepQueriesAsync()
    {
        foreach (var row in store.ListOutbox().Where(r =>
                     r.Status is nameof(OutboxState.AcceptedByProvider) or nameof(OutboxState.Unknown) && !r.CancelRequested))
        {
            var provider = StatusProviderResolver(row.Namespace);
            if (provider is null) continue;
            try
            {
                var tracked = await provider.QueryAsync(row.DeliveryId, serviceCancellationToken).ConfigureAwait(false);
                switch (tracked)
                {
                    case DeliveryTrackingState.Sent:
                        store.TransitionOutbox(row.DeliveryId, OutboxState.Sent);
                        break;
                    case DeliveryTrackingState.AcceptedByProvider:
                        store.TransitionOutbox(row.DeliveryId, OutboxState.AcceptedByProvider);
                        break;
                    case DeliveryTrackingState.Failed:
                        store.TransitionOutbox(row.DeliveryId, OutboxState.FailedPermanent, error: "provider reported failed");
                        break;
                    case DeliveryTrackingState.Cancelled:
                        store.TransitionOutbox(row.DeliveryId, OutboxState.Cancelled);
                        break;
                    case DeliveryTrackingState.Unknown:
                    case DeliveryTrackingState.NotFound:
                        // 不回退 Pending、不触发重发（M09）；NotFound 走人工
                        break;
                }
            }
            catch (Exception)
            {
                // 查询失败按退避重查（不重发）
            }
        }
    }
}

/// <summary>投递选项。</summary>
public sealed record OutboxOptions(
    int MaxRows = 200,
    TimeSpan TemporaryRetryBackoff = default,
    TimeSpan DeliveryTimeout = default)
{
    public TimeSpan TemporaryRetryBackoff { get; init; } = TemporaryRetryBackoff == default ? TimeSpan.FromSeconds(5) : TemporaryRetryBackoff;
    public TimeSpan DeliveryTimeout { get; init; } = DeliveryTimeout == default ? TimeSpan.FromMinutes(2) : DeliveryTimeout;
}

/// <summary>
/// 控制通道（§10.3）：/stop /status 独立路径——不进回合队列、不受限速熔断影响。
/// </summary>
public sealed class ControlCommands(
    MessagingStore store,
    SessionExecutionCoordinator coordinator,
    IdentitiesStore identities)
{
    /// <summary>/stop：登记停止意图并取消该聊天的排队任务；在飞回合经执行上下文 CancellationToken 传播。</summary>
    public string Stop(string channelId, string instanceId, string accountId, string chatId)
    {
        var route = store.FindRoute(channelId, instanceId, accountId, chatId);
        if (route is null) return "当前无进行中任务。";
        store.Db.Execute(
            "UPDATE inbox SET status='Cancelled', processed_at=$at WHERE chat_id=$chat AND status IN ('Pending','Processing')",
            ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$chat", chatId));
        return $"已请求停止会话 {route.SessionId} 的在飞任务。";
    }

    /// <summary>/status：队列/状态摘要。</summary>
    public string Status(string channelId, string instanceId, string accountId, string chatId)
    {
        var pending = store.PendingInboxCount();
        var outbox = store.ListOutbox().Where(r => r.ChatId == chatId).ToList();
        var sent = outbox.Count(r => r.Status == nameof(OutboxState.Sent));
        var queued = outbox.Count(r => r.Status is nameof(OutboxState.Pending) or nameof(OutboxState.AcceptedByProvider));
        var unknown = outbox.Count(r => r.Status == nameof(OutboxState.Unknown));
        var route = store.FindRoute(channelId, instanceId, accountId, chatId);
        return $"队列等待 {pending}；本会话已送达 {sent}、待发 {queued}、未知 {unknown}；会话：{route?.SessionId ?? "无"}";
    }
}
