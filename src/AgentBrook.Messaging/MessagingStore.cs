using System.Security.Cryptography;
using System.Text;
using AgentBrook.Messaging.Abstractions;
using AgentBrook.Runtime;

namespace AgentBrook.Messaging;

/// <summary>Inbox 行（权威队列）。</summary>
public sealed record InboxRow(
    long Id, string Namespace, string DedupKey, string ChannelId, string InstanceId,
    string AccountId, string ChatId, string ChatType, string SenderId, string? SenderName,
    bool IsSelf, bool IsBot, string Text, string MessageId, string Status, string? RejectReason,
    string? AttachmentsJson, string CreatedAt, string? ProcessedAt);

/// <summary>Outbox 行（持久投递任务；状态迁移只经 OutboxStateMachine）。</summary>
public sealed record OutboxRow(
    string DeliveryId, string Namespace, string SessionId, string TurnId,
    long AccountEpoch, string AccountId, string ChatId,
    string? Text, string? FilesJson, string? IdempotencyKey, string? Deadline,
    string Status, int Attempts, string? NextRetryAt, bool CancelRequested, string? CancelReason,
    string? PlatformMessageId, string? Error, string CreatedAt, string UpdatedAt);

/// <summary>路由绑定（会话 ↔ chat）。</summary>
public sealed record RouteRow(string SessionId, string ChannelId, string InstanceId,
    string AccountId, string ChatId, string PrincipalId, long AuthorizationEpoch, string CreatedAt);

/// <summary>
/// 领域表存储：inbox / outbox / routes / creations——与提交日志同库同事务边界（§3.3）。
/// 接收唯一键 = hash(ns|account|chat|messageId)（v4.1 命名空间规则完整形态）。
/// </summary>
public sealed class MessagingStore(ImDatabase db)
{
    public ImDatabase Db { get; } = db;

    static MessagingStore()
    {
    }

    /// <summary>建表（幂等；与 Runtime schema 同库共存）。</summary>
    public void EnsureSchema()
    {
        Db.Execute("""
            CREATE TABLE IF NOT EXISTS inbox (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              ns TEXT NOT NULL, dedup_key TEXT NOT NULL UNIQUE,
              channel_id TEXT NOT NULL, instance_id TEXT NOT NULL, account_id TEXT NOT NULL,
              chat_id TEXT NOT NULL, chat_type TEXT NOT NULL, sender_id TEXT NOT NULL, sender_name TEXT,
              is_self INTEGER NOT NULL, is_bot INTEGER NOT NULL,
              text TEXT NOT NULL, message_id TEXT NOT NULL,
              status TEXT NOT NULL,               -- Pending | Processing | Done | Cancelled
              reject_reason TEXT, attachments_json TEXT,
              created_at TEXT NOT NULL, processed_at TEXT);
            CREATE INDEX IF NOT EXISTS ix_inbox_status ON inbox(status);
            CREATE TABLE IF NOT EXISTS outbox (
              delivery_id TEXT PRIMARY KEY,
              ns TEXT NOT NULL, session_id TEXT NOT NULL, turn_id TEXT NOT NULL,
              account_epoch INTEGER NOT NULL, account_id TEXT NOT NULL, chat_id TEXT NOT NULL,
              text TEXT, files_json TEXT, idempotency_key TEXT, deadline TEXT,
              status TEXT NOT NULL,               -- OutboxState（迁移只经 OutboxStateMachine）
              attempts INTEGER NOT NULL DEFAULT 0, next_retry_at TEXT,
              cancel_requested INTEGER NOT NULL DEFAULT 0, cancel_reason TEXT,
              platform_message_id TEXT, error TEXT,
              created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_outbox_status ON outbox(status);
            CREATE TABLE IF NOT EXISTS routes (
              session_id TEXT PRIMARY KEY,
              channel_id TEXT NOT NULL, instance_id TEXT NOT NULL, account_id TEXT NOT NULL,
              chat_id TEXT NOT NULL, principal_id TEXT NOT NULL, authorization_epoch INTEGER NOT NULL,
              created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_routes_chat ON routes(channel_id, instance_id, account_id, chat_id);
            CREATE TABLE IF NOT EXISTS creations (
              session_id TEXT PRIMARY KEY,
              title TEXT NOT NULL, route_json TEXT NOT NULL,
              state TEXT NOT NULL,                 -- Pending | Committed
              created_at TEXT NOT NULL, committed_at TEXT);
            CREATE TABLE IF NOT EXISTS identities (
              principal_id TEXT NOT NULL,
              channel_id TEXT NOT NULL, instance_id TEXT NOT NULL, account_id TEXT NOT NULL,
              chat_id TEXT NOT NULL, sender_id TEXT NOT NULL,
              authorization_epoch INTEGER NOT NULL, scope TEXT NOT NULL, paired_at TEXT NOT NULL,
              PRIMARY KEY (channel_id, instance_id, account_id, chat_id, sender_id));
            """);
    }

    // ── Inbox ──

    /// <summary>持久化接收（唯一键冲突 → null=Duplicate）。容量满 → Busy。</summary>
    public InboxInsertResult InsertInbox(InboxRow row, int capacity)
    {
        var count = Convert.ToInt64(Db.CreateCommand("SELECT COUNT(*) FROM inbox WHERE status IN ('Pending','Processing')")
            .ExecuteScalar());
        if (count >= capacity) return new InboxInsertResult(null, InboxInsertOutcome.Busy);

        try
        {
            Db.Execute("""
                INSERT INTO inbox(ns, dedup_key, channel_id, instance_id, account_id, chat_id, chat_type,
                                  sender_id, sender_name, is_self, is_bot, text, message_id, status, attachments_json, created_at)
                VALUES($ns, $dk, $ch, $inst, $acc, $chat, $ctype, $sender, $sname, $self, $bot, $text, $mid, 'Pending', $att, $at)
                """,
                ("$ns", row.Namespace), ("$dk", row.DedupKey), ("$ch", row.ChannelId), ("$inst", row.InstanceId),
                ("$acc", row.AccountId), ("$chat", row.ChatId), ("$ctype", row.ChatType), ("$sender", row.SenderId),
                ("$sname", row.SenderName), ("$self", row.IsSelf ? 1 : 0), ("$bot", row.IsBot ? 1 : 0),
                ("$text", row.Text), ("$mid", row.MessageId), ("$att", row.AttachmentsJson),
                ("$at", DateTimeOffset.UtcNow.ToString("O")));
            var id = Convert.ToInt64(Db.CreateCommand("SELECT last_insert_rowid()").ExecuteScalar());
            return new InboxInsertResult(row with { Id = id }, InboxInsertOutcome.Accepted);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19) // UNIQUE
        {
            return new InboxInsertResult(null, InboxInsertOutcome.Duplicate);
        }
    }

    /// <summary>领取待执行（原子置 Processing）。</summary>
    public InboxRow? ClaimNextPendingInbox(string namespacePrefix)
    {
        using var cmd = Db.CreateCommand(
            "SELECT * FROM inbox WHERE status='Pending' AND ns LIKE $pfx ORDER BY id LIMIT 1",
            ("$pfx", namespacePrefix + "%"));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var row = ReadInbox(reader);
        var updated = Db.ExecuteNonQuery(
            "UPDATE inbox SET status='Processing' WHERE id = $id AND status='Pending'", ("$id", row.Id));
        return updated == 1 ? row : null;
    }

    /// <summary>启动补扫恢复：Processing（上次中断）→ Pending（唤醒信号丢失不丢消息，F02）。</summary>
    public int RescheduleInterruptedInbox() =>
        Db.ExecuteNonQuery("UPDATE inbox SET status='Pending' WHERE status='Processing'");

    /// <summary>
    /// 语音转写回写（doc/Voice/01 §3.2.0 P4）：单行持门 UPDATE，仅 Pending/Processing 行可写；
    /// 幂等命中查询同样读本列（attachments_json 内 Transcript）。返回受影响行数（0=行已推进/清理）。
    /// </summary>
    public int UpdateInboxAttachmentsJson(long id, string attachmentsJson) =>
        Db.ExecuteNonQuery(
            "UPDATE inbox SET attachments_json=$att WHERE id=$id AND status IN ('Pending','Processing')",
            ("$att", attachmentsJson), ("$id", id));

    public InboxRow? GetInboxByDedupKey(string dedupKey)
    {
        using var cmd = Db.CreateCommand("SELECT * FROM inbox WHERE dedup_key = $dk", ("$dk", dedupKey));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadInbox(reader) : null;
    }

    public int PendingInboxCount()
    {
        using var cmd = Db.CreateCommand("SELECT COUNT(*) FROM inbox WHERE status='Pending'");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static string ComputeDedupKey(string ns, string accountId, string chatId, string messageId)
    {
        var bytes = Encoding.UTF8.GetBytes($"{ns}|{accountId}|{chatId}|{messageId}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static InboxRow ReadInbox(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        Convert.ToInt64(r["id"]), (string)r["ns"], (string)r["dedup_key"], (string)r["channel_id"],
        (string)r["instance_id"], (string)r["account_id"], (string)r["chat_id"], (string)r["chat_type"],
        (string)r["sender_id"], r["sender_name"] as string, Convert.ToInt64(r["is_self"]) != 0,
        Convert.ToInt64(r["is_bot"]) != 0, (string)r["text"], (string)r["message_id"],
        (string)r["status"], r["reject_reason"] as string, r["attachments_json"] as string,
        (string)r["created_at"], r["processed_at"] as string);

    // ── Routes / creations ──

    public void SaveRoute(RouteRow route)
    {
        Db.Execute("""
            INSERT INTO routes(session_id, channel_id, instance_id, account_id, chat_id, principal_id, authorization_epoch, created_at)
            VALUES($sid, $ch, $inst, $acc, $chat, $p, $ep, $at)
            ON CONFLICT(session_id) DO UPDATE SET
              channel_id=excluded.channel_id, instance_id=excluded.instance_id, account_id=excluded.account_id,
              chat_id=excluded.chat_id, principal_id=excluded.principal_id, authorization_epoch=excluded.authorization_epoch
            """,
            ("$sid", route.SessionId), ("$ch", route.ChannelId), ("$inst", route.InstanceId),
            ("$acc", route.AccountId), ("$chat", route.ChatId), ("$p", route.PrincipalId),
            ("$ep", route.AuthorizationEpoch), ("$at", route.CreatedAt));
    }

    public RouteRow? FindRoute(string channelId, string instanceId, string accountId, string chatId)
    {
        using var cmd = Db.CreateCommand(
            "SELECT * FROM routes WHERE channel_id=$ch AND instance_id=$inst AND account_id=$acc AND chat_id=$chat ORDER BY created_at DESC LIMIT 1",
            ("$ch", channelId), ("$inst", instanceId), ("$acc", accountId), ("$chat", chatId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new RouteRow((string)reader["session_id"], (string)reader["channel_id"], (string)reader["instance_id"],
            (string)reader["account_id"], (string)reader["chat_id"], (string)reader["principal_id"],
            Convert.ToInt64(reader["authorization_epoch"]), (string)reader["created_at"]);
    }

    /// <summary>解除路由绑定（用户删除 IM 会话 = 归档：同一 chat 的新消息开启新会话，不再被旧墓碑永拒）。</summary>
    public void DeleteRoute(string sessionId)
    {
        Db.Execute("DELETE FROM routes WHERE session_id=$sid", ("$sid", sessionId));
    }

    public RouteRow? GetRoute(string sessionId)
    {
        using var cmd = Db.CreateCommand("SELECT * FROM routes WHERE session_id=$sid", ("$sid", sessionId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new RouteRow((string)reader["session_id"], (string)reader["channel_id"], (string)reader["instance_id"],
            (string)reader["account_id"], (string)reader["chat_id"], (string)reader["principal_id"],
            Convert.ToInt64(reader["authorization_epoch"]), (string)reader["created_at"]);
    }

    /// <summary>会话创建意图（§11.2：先落库再创建；重复恢复不重复建）。</summary>
    public bool InsertCreationIntent(string sessionId, string title, string routeJson)
    {
        try
        {
            Db.Execute(
                "INSERT INTO creations(session_id, title, route_json, state, created_at) VALUES($sid, $t, $r, 'Pending', $at)",
                ("$sid", sessionId), ("$t", title), ("$r", routeJson), ("$at", DateTimeOffset.UtcNow.ToString("O")));
            return true;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return false;   // 意图已存在（幂等）
        }
    }

    public void MarkCreationCommitted(string sessionId)
    {
        Db.Execute("UPDATE creations SET state='Committed', committed_at=$at WHERE session_id=$sid",
            ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$sid", sessionId));
    }

    public List<(string SessionId, string Title, string RouteJson)> ListPendingCreations()
    {
        using var cmd = Db.CreateCommand("SELECT session_id, title, route_json FROM creations WHERE state='Pending'");
        using var reader = cmd.ExecuteReader();
        var list = new List<(string, string, string)>();
        while (reader.Read())
            list.Add(((string)reader["session_id"], (string)reader["title"], (string)reader["route_json"]));
        return list;
    }

    // ── Outbox ──

    public void InsertOutbox(OutboxRow row)
    {
        Db.Execute("""
            INSERT INTO outbox(delivery_id, ns, session_id, turn_id, account_epoch, account_id, chat_id,
                               text, files_json, idempotency_key, deadline, status, attempts, cancel_requested, created_at, updated_at)
            VALUES($id, $ns, $sid, $tid, $ep, $acc, $chat, $text, $files, $idem, $dl, $st, 0, $cancel, $at, $at)
            """,
            ("$id", row.DeliveryId), ("$ns", row.Namespace), ("$sid", row.SessionId), ("$tid", row.TurnId),
            ("$ep", row.AccountEpoch), ("$acc", row.AccountId), ("$chat", row.ChatId),
            ("$text", row.Text), ("$files", row.FilesJson), ("$idem", row.IdempotencyKey), ("$dl", row.Deadline),
            ("$st", row.Status), ("$cancel", row.CancelRequested ? 1 : 0),
            ("$at", DateTimeOffset.UtcNow.ToString("O")));
    }

    public OutboxRow? GetOutbox(string deliveryId)
    {
        using var cmd = Db.CreateCommand("SELECT * FROM outbox WHERE delivery_id=$id", ("$id", deliveryId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadOutbox(reader) : null;
    }

    public List<OutboxRow> ListOutbox(string? status = null)
    {
        using var cmd = status is null
            ? Db.CreateCommand("SELECT * FROM outbox ORDER BY created_at")
            : Db.CreateCommand("SELECT * FROM outbox WHERE status=$st ORDER BY created_at", ("$st", status));
        using var reader = cmd.ExecuteReader();
        var list = new List<OutboxRow>();
        while (reader.Read()) list.Add(ReadOutbox(reader));
        return list;
    }

    /// <summary>状态迁移（经契约状态机；非法迁移拒绝并保持原状态——只允许合法迁移 §10.1）。</summary>
    public bool TransitionOutbox(string deliveryId, OutboxState to, string? platformMessageId = null,
        string? error = null, TimeSpan? retryAfter = null)
    {
        var row = GetOutbox(deliveryId);
        if (row is null) return false;
        var current = Enum.Parse<OutboxState>(row.Status);
        if (!OutboxStateMachine.TryTransition(ref current, to)) return false;

        Db.Execute("""
            UPDATE outbox SET status=$st, platform_message_id=COALESCE($pm, platform_message_id),
                              error=$err, next_retry_at=$retry, updated_at=$at
            WHERE delivery_id=$id
            """,
            ("$st", to.ToString()), ("$pm", platformMessageId), ("$err", error),
            ("$retry", retryAfter is null ? null : (DateTimeOffset.UtcNow + retryAfter.Value).ToString("O")),
            ("$id", deliveryId), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        return true;
    }

    /// <summary>取消意图先持久化（v4.3：先落库，再经通道下发取消）。</summary>
    public bool PersistCancelIntent(string deliveryId, string reason)
    {
        var row = GetOutbox(deliveryId);
        if (row is null) return false;
        Db.Execute("UPDATE outbox SET cancel_requested=1, cancel_reason=$r, updated_at=$at WHERE delivery_id=$id",
            ("$r", reason), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$id", deliveryId));
        return true;
    }

    public int IncrementAttempts(string deliveryId)
    {
        return Db.Execute("UPDATE outbox SET attempts = attempts + 1, updated_at=$at WHERE delivery_id=$id",
            ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$id", deliveryId));
    }

    private static OutboxRow ReadOutbox(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        (string)r["delivery_id"], (string)r["ns"], (string)r["session_id"], (string)r["turn_id"],
        Convert.ToInt64(r["account_epoch"]), (string)r["account_id"], (string)r["chat_id"],
        r["text"] as string, r["files_json"] as string, r["idempotency_key"] as string, r["deadline"] as string,
        (string)r["status"], Convert.ToInt32(r["attempts"]), r["next_retry_at"] as string,
        Convert.ToInt64(r["cancel_requested"]) != 0, r["cancel_reason"] as string,
        r["platform_message_id"] as string, r["error"] as string,
        (string)r["created_at"], (string)r["updated_at"]);
}

public enum InboxInsertOutcome { Accepted, Duplicate, Busy }

public sealed record InboxInsertResult(InboxRow? Row, InboxInsertOutcome Outcome);
