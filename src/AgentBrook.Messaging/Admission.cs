using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentBrook.Messaging.Abstractions;
using AgentBrook.Runtime;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Messaging;

/// <summary>配对身份（identities；P0 单信任域：全部配对主体共享远程工作区/记忆，§13/§14）。</summary>
public sealed record PairedIdentity(
    string PrincipalId, string ChannelId, string InstanceId, string AccountId, string ChatId,
    string SenderId, long AuthorizationEpoch, DisclosureScope Scope, string PairedAt);

/// <summary>
/// 配对身份库（默认拒绝：空白名单=全拒，§14）。撤权 = epoch+1（执行前/工具前/发送前重验）。
/// </summary>
public sealed class IdentitiesStore(ImDatabase db)
{
    /// <summary>配对（幂等：同 chat 重复申请合并；返回现行 epoch）。</summary>
    public long Pair(PairedIdentity identity)
    {
        var existing = Find(identity.ChannelId, identity.InstanceId, identity.AccountId, identity.ChatId, identity.SenderId);
        if (existing is not null) return existing.AuthorizationEpoch;
        db.Execute("""
            INSERT OR IGNORE INTO identities(principal_id, channel_id, instance_id, account_id, chat_id, sender_id,
                                             authorization_epoch, scope, paired_at)
            VALUES($p, $ch, $inst, $acc, $chat, $sender, 1, $scope, $at)
            """,
            ("$p", identity.PrincipalId), ("$ch", identity.ChannelId), ("$inst", identity.InstanceId),
            ("$acc", identity.AccountId), ("$chat", identity.ChatId), ("$sender", identity.SenderId),
            ("$scope", identity.Scope.ToString()), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        return 1;
    }

    public PairedIdentity? Find(string channelId, string instanceId, string accountId, string chatId, string senderId)
    {
        using var cmd = db.CreateCommand(
            "SELECT * FROM identities WHERE channel_id=$ch AND instance_id=$inst AND account_id=$acc AND chat_id=$chat AND sender_id=$sender",
            ("$ch", channelId), ("$inst", instanceId), ("$acc", accountId), ("$chat", chatId), ("$sender", senderId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new PairedIdentity(
            (string)reader["principal_id"], (string)reader["channel_id"], (string)reader["instance_id"],
            (string)reader["account_id"], (string)reader["chat_id"], (string)reader["sender_id"],
            Convert.ToInt64(reader["authorization_epoch"]),
            Enum.Parse<DisclosureScope>((string)reader["scope"]), (string)reader["paired_at"]);
    }

    /// <summary>撤权（§11.6）：epoch+1；旧授权全部失效。</summary>
    public void Unpair(string channelId, string instanceId, string accountId, string chatId, string senderId)
    {
        db.Execute("""
            UPDATE identities SET authorization_epoch = authorization_epoch + 1
            WHERE channel_id=$ch AND instance_id=$inst AND account_id=$acc AND chat_id=$chat AND sender_id=$sender
            """,
            ("$ch", channelId), ("$inst", instanceId), ("$acc", accountId), ("$chat", chatId), ("$sender", senderId));
    }

    /// <summary>账号更换即时隔离（§11.6）：入站 accountId 与既有绑定不符即拒绝（不等 /health 周期）。</summary>
    public bool AccountChanged(string channelId, string instanceId, string accountId)
    {
        using var cmd = db.CreateCommand(
            "SELECT COUNT(*) FROM identities WHERE channel_id=$ch AND instance_id=$inst AND account_id != $acc",
            ("$ch", channelId), ("$inst", instanceId), ("$acc", accountId));
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
}

/// <summary>
/// 入站接收管道（IImInboundSink 实现；设计 §5 顺序）：
/// 0 防回声 → 1 协议校验（身份归一）→ 2 主体准入（默认拒绝+群禁用+账号即时隔离）→
/// 3 配额 → 4 持久化 Inbox（唯一键幂等；容量满 Busy）→ 5 判定（通道据此推进游标）。
/// </summary>
public sealed class InboundPipeline(
    MessagingStore store,
    IdentitiesStore identities,
    ImEndpointIdentity boundIdentity,
    InboundQuotas quotas,
    Action<InboxRow>? onAccepted = null,
    bool autoPairPrivateChats = false) : IImInboundSink
{
    private readonly ConcurrentDictionary<string, Deque> _recentTimestamps = new();

    public ImEndpointIdentity Identity { get; } = boundIdentity;

    public async Task<ImAcceptResult> AcceptInboundAsync(ImInboundMessage message, CancellationToken ct)
    {
        await store.Db.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return AcceptCore(message);
        }
        finally
        {
            store.Db.Gate.Release();
        }
    }

    private ImAcceptResult AcceptCore(ImInboundMessage message)
    {
        // 1 身份归一校验：消息声称的通道/实例必须与宿主绑定一致（防伪造/误配/旧实例迟到提交）
        if (message.ChannelId != Identity.PluginId || message.ChannelInstanceId != Identity.InstanceId)
            return ImAcceptResult.Rejected(ImErrorCodes.IdentityMismatch);

        // 0 防回声：IsSelf → Duplicate（已决，通道可确认）
        if (message.IsSelf) return ImAcceptResult.Duplicate();

        // 2 主体准入（默认拒绝）
        if (message.ChatType is ImChatType.Group or ImChatType.Channel or ImChatType.Unknown)
            return ImAcceptResult.Rejected(ImErrorCodes.GroupDisabled);

        if (identities.AccountChanged(message.ChannelId, message.ChannelInstanceId, message.AccountId))
            return ImAcceptResult.Rejected(ImErrorCodes.AccountMismatch);

        var identity = identities.Find(message.ChannelId, message.ChannelInstanceId, message.AccountId,
            message.ChatId, message.SenderId);
        if (identity is null)
        {
            // 受控自动配对（Helper 侧开关；域默认关）：私聊首条消息即配对（个人助理形态，防"静默拒绝"
            // 无配对 UI 可用的死局）；群/频道仍走 GroupDisabled。PrincipalId 取稳定 sender id（P0 单信任域）。
            if (autoPairPrivateChats && message.ChatType == ImChatType.Private)
            {
                identities.Pair(new PairedIdentity(message.SenderId, message.ChannelId, message.ChannelInstanceId,
                    message.AccountId, message.ChatId, message.SenderId, 1,
                    DisclosureScope.PairedPrivate, DateTimeOffset.UtcNow.ToString("O")));
                identity = identities.Find(message.ChannelId, message.ChannelInstanceId, message.AccountId,
                    message.ChatId, message.SenderId);
            }
            if (identity is null)
                return ImAcceptResult.Rejected(ImErrorCodes.NotPaired);   // 触发本机配对流程（UI 侧）
        }

        // 3 配额：每主体接收速率（10/min → Busy+RetryAfter；§10.2）
        var bucketKey = $"{message.SenderId}@{message.ChatId}";
        var deque = _recentTimestamps.GetOrAdd(bucketKey, _ => new Deque());
        if (!deque.TryRecord(DateTimeOffset.UtcNow, quotas.MaxPerMinute))
            return ImAcceptResult.Busy(TimeSpan.FromSeconds(60));

        // 4 持久化（权威队列；唯一键幂等；容量满 Busy——未接收，通道不得确认）
        var ns = $"{message.ChannelId}+{message.ChannelInstanceId}";
        var dedupKey = MessagingStore.ComputeDedupKey(ns, message.AccountId, message.ChatId, message.MessageId);
        var row = new InboxRow(0, ns, dedupKey, message.ChannelId, message.ChannelInstanceId, message.AccountId,
            message.ChatId, message.ChatType.ToString(), message.SenderId, message.SenderName,
            message.IsSelf, message.IsBot, message.Text, message.MessageId, "Pending", null,
            SerializeAttachments(message.Attachments), DateTimeOffset.UtcNow.ToString("O"), null);
        var result = store.InsertInbox(row, quotas.InboxCapacity);
        return result.Outcome switch
        {
            InboxInsertOutcome.Accepted => AcceptAndNotify(result.Row!),
            InboxInsertOutcome.Duplicate => ImAcceptResult.Duplicate(),
            _ => ImAcceptResult.Busy(TimeSpan.FromSeconds(3)),
        };
    }

    private ImAcceptResult AcceptAndNotify(InboxRow row)
    {
        onAccepted?.Invoke(row);
        return ImAcceptResult.Accepted();
    }

    private static string? SerializeAttachments(IReadOnlyList<ImAttachmentDescriptor>? attachments) =>
        attachments is null || attachments.Count == 0 ? null : ImJson.Serialize(attachments);

    /// <summary>滑动窗口速率记录。</summary>
    private sealed class Deque
    {
        private readonly object _gate = new();
        private readonly Queue<DateTimeOffset> _times = new();

        public bool TryRecord(DateTimeOffset now, int maxPerMinute)
        {
            lock (_gate)
            {
                while (_times.Count > 0 && now - _times.Peek() > TimeSpan.FromMinutes(1)) _times.Dequeue();
                if (_times.Count >= maxPerMinute) return false;
                _times.Enqueue(now);
                return true;
            }
        }
    }
}

/// <summary>接收侧配额（与执行配额分离 §5）。</summary>
public sealed record InboundQuotas(int InboxCapacity = 1000, int MaxPerMinute = 10);

/// <summary>
/// 执行配额（§5：全局在飞 ≤2、待执行 ≤50 是执行配额；超出的留在 DB Pending）。
/// </summary>
public sealed class ExecutionQuotas : IDisposable
{
    public const int MaxGlobalInFlight = 2;
    public const int MaxQueued = 50;

    private readonly SemaphoreSlim _slots = new(MaxGlobalInFlight, MaxGlobalInFlight);
    private int _queued;

    public int QueuedCount => Volatile.Read(ref _queued);

    /// <summary>排队（队列满 → false，消息留库 Pending）。</summary>
    public bool TryEnqueue() => Interlocked.Increment(ref _queued) <= MaxQueued;

    public void DequeueQueued() => Interlocked.Decrement(ref _queued);

    public Task WaitSlotAsync(CancellationToken ct) => _slots.WaitAsync(ct);

    public void ReleaseSlot() => _slots.Release();

    public void Dispose() => _slots.Dispose();
}
