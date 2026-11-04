using System.Collections.Concurrent;
using AgentBrook.Messaging.Abstractions;
using AgentBrook.Runtime;
using System.Text;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Messaging;

/// <summary>会话工厂端口（S4 由 TestHost 提供 fake 注册表；S7 由 Agent.Core 适配）。</summary>
public interface ISessionFactory
{
    /// <summary>预分配会话 id 并创建（返回已初始化 g1/r1 的会话）。</summary>
    Task CreateSessionAsync(string sessionId, string title, CancellationToken ct);

    /// <summary>会话是否存在（路由/恢复对账用）。</summary>
    bool Exists(string sessionId);

    /// <summary>删除会话（测试用）。</summary>
    void DeleteSession(string sessionId);
}

/// <summary>回合执行完成事件（UI/测试订阅）。</summary>
public sealed record TurnCompletedNotice(string SessionId, string TurnId, string InboxDedupKey, bool Completed, string? Error);

/// <summary>
/// 回合调度器（设计 §5/§6/§8）：唤醒 → 领取 Inbox（权威队列）→ 会话路由/创建意图 →
/// 租约（读最新→执行→提交）→ 完成事务（turn Completed + inbox Done + outbox 创建，§3.3 单一事务）→
/// 出站披露/分片。
/// </summary>
public sealed class TurnDispatcher(
    MessagingStore store,
    SessionExecutionCoordinator coordinator,
    CommitJournal journal,
    ISessionFactory sessions,
    Func<TurnExecutionContext, ITurnExecutor> executorFactory,
    IApprovalPort approvals,
    DisclosurePolicy disclosure,
    ImChannelCapabilities capabilities,
    ExecutionQuotas quotas,
    CancellationToken serviceCancellationToken)
{
    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>诊断：最近一次回合处理异常（测试失败定位用）。</summary>
    public Exception? LastDispatcherError { get; private set; }

    public event Action<TurnCompletedNotice>? TurnCompleted;

    /// <summary>唤醒信号（内存；丢失由补扫兜底 F02）。</summary>
    public void Wake() => _wake.Release();

    /// <summary>主循环（宿主后台任务驱动）。所有 DB 触碰持连接门（共享 SqliteConnection 非线程安全）。</summary>
    public async Task RunAsync()
    {
        while (!serviceCancellationToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(serviceCancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            while (await PendingCountAsync().ConfigureAwait(false) > 0)
            {
                if (serviceCancellationToken.IsCancellationRequested) return;
                if (!await TryProcessOneAsync().ConfigureAwait(false)) break;
            }
        }
    }

    private async Task<int> PendingCountAsync()
    {
        await store.Db.Gate.WaitAsync(serviceCancellationToken).ConfigureAwait(false);
        try
        {
            return store.PendingInboxCount();
        }
        finally
        {
            store.Db.Gate.Release();
        }
    }

    private async Task<bool> TryProcessOneAsync()
    {
        // 执行配额：队列满 → 留库 Pending（M03/M05）
        if (!quotas.TryEnqueue())
        {
            await Task.Delay(200, serviceCancellationToken).ConfigureAwait(false);
            return false;
        }
        quotas.DequeueQueued();

        InboxRow? row = null;
        try
        {
            // 领取（持门）
            await store.Db.Gate.WaitAsync(serviceCancellationToken).ConfigureAwait(false);
            try
            {
                row = store.ClaimNextPendingInbox("");
            }
            finally
            {
                store.Db.Gate.Release();
            }
            if (row is null) return false;

            await quotas.WaitSlotAsync(serviceCancellationToken).ConfigureAwait(false);
            try
            {
                await ExecuteTurnAsync(row).ConfigureAwait(false);
            }
            finally
            {
                quotas.ReleaseSlot();
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            if (row is not null) await RescheduleAsync(row).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            LastDispatcherError = ex;
            Console.Error.WriteLine($"[im-dispatch] 回合处理失败（将回 Pending 退避重试）：{ex.Message}");
            if (row is not null) await RescheduleAsync(row).ConfigureAwait(false);   // 失败回 Pending（留库，可重试/可查）
            await Task.Delay(500, serviceCancellationToken).ConfigureAwait(false);   // 失败退避：防无延迟死循环垃圾风暴
            return true;
        }
    }

    private async Task RescheduleAsync(InboxRow row)
    {
        await store.Db.Gate.WaitAsync(serviceCancellationToken).ConfigureAwait(false);
        try
        {
            store.Db.Execute("UPDATE inbox SET status='Pending' WHERE id=$id AND status='Processing'", ("$id", row.Id));
        }
        finally
        {
            store.Db.Gate.Release();
        }
    }

    /// <summary>单条消息的完整回合（路由 → 创建意图 → 租约 → 执行 → 提交事务）。</summary>
    public async Task ExecuteTurnAsync(InboxRow row)
    {
        // ── 路由：既有会话或创建意图（先落库再创建，§11.2）──
        var route = store.FindRoute(row.ChannelId, row.InstanceId, row.AccountId, row.ChatId);
        string sessionId = "";   // 下方两分支必赋一（既有路由 / 新建）
        if (route is not null)
        {
            sessionId = route.SessionId;
            if (!sessions.Exists(sessionId))
            {
                if (journal.TombstoneBlocksAsync(sessionId, 1).GetAwaiter().GetResult())
                {
                    // 会话已删除（用户删除 IM 会话 = 归档）：解除 chat→旧会话路由，本条消息走新建路径开新会话。
                    // 旧 sessionId 墓碑语义不变（旧回合迟到物不复活，M04 完整性保留）。
                    store.DeleteRoute(sessionId);
                    route = null;
                }
                else
                {
                    // 路由丢失：仅恢复合法创建（M04：不重复建、不复活已删除会话）
                    await RecreateSessionAsync(row, sessionId).ConfigureAwait(false);
                }
            }
        }
        if (route is null)
        {
            sessionId = "im-" + Guid.NewGuid().ToString("N")[..12];
            var routeJson = ImJson.Serialize(new
            {
                row.ChannelId, row.InstanceId, row.AccountId, row.ChatId,
                principal = row.SenderId, title = $"{row.SenderName ?? row.SenderId}［{row.ChannelId}］",
            });
            store.InsertCreationIntent(sessionId, $"{row.SenderName ?? row.SenderId}［{row.ChannelId}］", routeJson);
            await sessions.CreateSessionAsync(sessionId, $"{row.SenderName ?? row.SenderId}［{row.ChannelId}］",
                serviceCancellationToken).ConfigureAwait(false);
            store.SaveRoute(new RouteRow(sessionId, row.ChannelId, row.InstanceId, row.AccountId, row.ChatId,
                row.SenderId, 1, DateTimeOffset.UtcNow.ToString("O")));
            store.MarkCreationCommitted(sessionId);
        }

        // ── 租约：读最新 → 执行 → 提交（D3；Agent 未就绪语义由宿主门控，留库即不丢）──
        await using var lease = await coordinator.AcquireAsync(sessionId, serviceCancellationToken).ConfigureAwait(false);
        var current = coordinator.ReadCurrentVersion(sessionId);
        var turnId = "t-" + Guid.NewGuid().ToString("N")[..12];

        var context = new TurnExecutionContext(
            sessionId, current.Generation, current.Revision, turnId, TurnOrigin.Im,
            new PrincipalRef(row.SenderId, 1, DisclosureScope.PairedPrivate),
            new ReplyTarget(row.ChannelId, row.InstanceId, row.AccountId, row.ChatId),
            PermissionPolicy.P0Default, approvals, row.Text,
            Attachments: ParseAttachments(row));   // S8 附件：统一管道描述符（物化由通道插件实现）
        await journal.Db.Gate.WaitAsync(serviceCancellationToken).ConfigureAwait(false);
        try
        {
            await journal.BeginTurnAsync(context).ConfigureAwait(false);
        }
        finally
        {
            journal.Db.Gate.Release();
        }

        var executor = executorFactory(context);
        TurnOutcome outcome;
        try
        {
            outcome = await executor.ExecuteAsync(context, serviceCancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            journal.CancelTurnAsync(turnId, "service cancelled").GetAwaiter().GetResult();
            await RescheduleAsync(row).ConfigureAwait(false);
            throw;
        }

        // ── 完成事务：turn Completed + inbox Done + outbox 创建（同库单一事务；F11 语义）──
        var events = outcome.Events;
        // 同权执行期间，主 Agent 持久化（同一逻辑写者的顺序写入）可能已推进版本——
        // 提交前重读最新 revision 作为 CAS 基线（D03 防护不变：他者写入仍会被拒）。
        var latestVersion = coordinator.ReadCurrentVersion(sessionId);
        context = context with { Generation = latestVersion.Generation, ExpectedRevision = latestVersion.Revision };
        // S7：真实执行器（MAF）回传序列化快照；无则退回固定 JSON 形态（S4 fake 语义）
        var snapshot = outcome.SnapshotPayload
            ?? EncodingSnapshot(sessionId, latestVersion.Generation, latestVersion.Revision + 1, events, outcome);
        var outboxPlan = BuildOutboxPlan(row, outcome, route ?? store.GetRoute(sessionId));

        await journal.Db.Gate.WaitAsync(serviceCancellationToken).ConfigureAwait(false);
        try
        {
            await journal.Db.InTransactionAsync(async tx =>
            {
                tx.Execute("UPDATE turns SET status='Completed', completed_at=$at WHERE turn_id=$tid",
                    ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$tid", turnId));
                tx.Execute("UPDATE inbox SET status='Done', processed_at=$at WHERE id=$id",
                    ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$id", row.Id));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            // 提交协议（CURRENT 切换 + 投影 + commits 账务 + outbox 落库同事务参与）
            var receipt = await journal.CommitPendingAsync(
                context, events, snapshot, outcome.AnswerText, outboxPlan,
                participant: tx =>
                {
                    var fragments = DecodeOutboxPlan(outboxPlan);
                    var filesJson = outcome.Files is { Count: > 0 }
                        ? ImJson.Serialize(outcome.Files.Select(f => f.Content).ToList())
                        : null;
                    for (var i = 0; i < fragments.Count; i++)
                        WriteOutboxInTx(tx, fragments[i], row, sessionId, turnId,
                            filesJson: i == fragments.Count - 1 ? filesJson : null);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
        }
        finally
        {
            journal.Db.Gate.Release();
        }

        TurnCompleted?.Invoke(new TurnCompletedNotice(sessionId, turnId, row.DedupKey, outcome.Completed, outcome.Error));
    }

    private async Task RecreateSessionAsync(InboxRow row, string sessionId)
    {
        await sessions.CreateSessionAsync(sessionId, $"{row.SenderName ?? row.SenderId}［{row.ChannelId}］",
            serviceCancellationToken).ConfigureAwait(false);
    }

    private void CompleteInboxOnly(InboxRow row) =>
        store.Db.Execute("UPDATE inbox SET status='Done', processed_at=$at WHERE id=$id",
            ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$id", row.Id));

    private static byte[] EncodingSnapshot(string sessionId, long generation, long revision,
        IReadOnlyList<TurnEvent> events, TurnOutcome outcome)
    {
        // S2 快照形态：固定 JSON 字节（S7 换 MAF 适配，存储层无感）
        var doc = new
        {
            sessionId,
            generation,
            revision,
            answer = outcome.AnswerText,
            tail = events.LastOrDefault()?.Text,
        };
        return Encoding.UTF8.GetBytes(ImJson.Serialize(doc));
    }

    /// <summary>Outbox 计划（提交协议步骤 2b 的 outbox 计划字段）：披露→分片→每片一条。</summary>
    private string BuildOutboxPlan(InboxRow row, TurnOutcome outcome, RouteRow? route)
    {
        if (!outcome.Completed || outcome.AnswerText is null || route is null)
            return "[]";
        var fragments = disclosure.PlanOutbound(route.PrincipalId, outcome.AnswerText, capabilities);
        return ImJson.Serialize(fragments);
    }

    private static List<string> DecodeOutboxPlan(string plan) =>
        ImJson.Deserialize<List<string>>(plan);

    private void WriteOutboxInTx(ImTransaction tx, string fragmentText, InboxRow row, string sessionId, string turnId,
        string? filesJson = null)
    {
        var deliveryId = $"ob-{Guid.NewGuid():N}"[..16];
        // 直接 SQL 写入（同事务参与——§3.3；不另开连接）
        tx.Execute("""
            INSERT INTO outbox(delivery_id, ns, session_id, turn_id, account_epoch, account_id, chat_id,
                               text, files_json, idempotency_key, deadline, status, attempts, cancel_requested, created_at, updated_at)
            VALUES($id, $ns, $sid, $tid, 1, $acc, $chat, $text, $files, $idem, NULL, 'Pending', 0, 0, $at, $at)
            """,
            ("$id", deliveryId), ("$ns", $"{row.ChannelId}+{row.InstanceId}"), ("$sid", sessionId), ("$tid", turnId),
            ("$acc", row.AccountId), ("$chat", row.ChatId), ("$text", fragmentText),
            ("$files", filesJson), ("$idem", $"idem-{deliveryId}"), ("$at", DateTimeOffset.UtcNow.ToString("O")));
    }

    /// <summary>解析入站附件描述符（统一管道 §8.1；无附件 → null）。</summary>
    private static IReadOnlyList<TurnExecutionAttachment>? ParseAttachments(InboxRow row)
    {
        if (string.IsNullOrWhiteSpace(row.AttachmentsJson)) return null;
        var descriptors = ImJson.Deserialize<List<ImAttachmentDescriptor>>(row.AttachmentsJson);
        if (descriptors is null || descriptors.Count == 0) return null;
        return [.. descriptors.Select(d => new TurnExecutionAttachment(
            row.ChannelId, row.InstanceId, d.Provider, d.RemoteId, d.Name, d.MediaType,
            (int)d.Kind, d.DeclaredBytes))];
    }
}

/// <summary>披露策略（设计 §9）：IM 来源默认允许回传；本地来源默认不外发；越界内容默认审批。</summary>
public sealed class DisclosurePolicy
{
    /// <summary>产出分片文本序列（M07：长文本分片+续标，字符边界；续标宽度动态预留）。</summary>
    public List<string> PlanOutbound(string principalId, string text, ImChannelCapabilities caps)
    {
        if (string.IsNullOrEmpty(text)) return [];
        if (text.Length <= caps.MaxTextChars) return [text];

        // 预估分片数 → 续标 " (n/N)" 宽度 → 有效载荷上限
        var estimate = text.Length / Math.Max(1, caps.MaxTextChars - 8) + 2;
        var markerWidth = $" ({estimate}/{estimate})".Length + 2;
        var limit = caps.MaxTextChars - markerWidth;
        if (limit < 1) limit = caps.MaxTextChars / 2;

        var raw = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            var take = Math.Min(limit, text.Length - index);
            // 避免把代理对（surrogate pair）切开（字符边界）
            if (index + take < text.Length && char.IsHighSurrogate(text[index + take - 1])) take--;
            raw.Add(text[index..(index + take)]);
            index += take;
        }

        return [.. raw.Select((f, i) => raw.Count == 1 ? f : $"{f} ({i + 1}/{raw.Count})")];
    }
}
