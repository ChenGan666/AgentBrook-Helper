using System.Text.Json;
using AgentBrook.A2A.Core.Peers;
using AgentBrook.A2A.Core.Reputation;
using AgentBrook.A2A.Core.Sessions;
using AgentBrook.A2A.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace AgentBrook.A2A.Core.Persistence;

public sealed class A2AStore : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _initialized;

    public A2AStore(string databasePath)
    {
        DatabasePath = databasePath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS peers (
                    node_id TEXT PRIMARY KEY,
                    display_name TEXT NULL,
                    trust_state INTEGER NOT NULL,
                    public_key TEXT NULL,
                    profile_json TEXT NULL,
                    profile_version INTEGER NOT NULL DEFAULT 0,
                    endpoints_json TEXT NOT NULL DEFAULT '[]',
                    last_seen_at INTEGER NULL
                );

                CREATE TABLE IF NOT EXISTS sessions (
                    session_id TEXT PRIMARY KEY,
                    kind INTEGER NOT NULL,
                    context_id TEXT NULL,
                    linked_task_id TEXT NULL,
                    created_by TEXT NOT NULL,
                    coordinator_node_id TEXT NULL,
                    members_json TEXT NOT NULL DEFAULT '[]',
                    status INTEGER NOT NULL,
                    last_sequence INTEGER NOT NULL DEFAULT 0,
                    group_key_epoch INTEGER NOT NULL DEFAULT 1,
                    created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS events (
                    row_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id TEXT NOT NULL UNIQUE,
                    session_id TEXT NOT NULL,
                    sender_node_id TEXT NOT NULL,
                    timestamp INTEGER NOT NULL,
                    sequence INTEGER NOT NULL,
                    type INTEGER NOT NULL,
                    payload_json TEXT NULL,
                    prev_hash TEXT NULL,
                    signature TEXT NULL,
                    UNIQUE(session_id, sequence)
                );
                CREATE INDEX IF NOT EXISTS idx_events_session ON events(session_id, sequence);

                CREATE TABLE IF NOT EXISTS tasks (
                    task_id TEXT PRIMARY KEY,
                    group_id TEXT NULL,
                    coordinator_node_id TEXT NOT NULL,
                    manifest_json TEXT NOT NULL,
                    state INTEGER NOT NULL,
                    acceptances_json TEXT NOT NULL DEFAULT '[]',
                    deliveries_json TEXT NOT NULL DEFAULT '[]',
                    failure_reason TEXT NULL,
                    created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ratings (
                    rating_id TEXT PRIMARY KEY,
                    task_id TEXT NOT NULL,
                    from_node_id TEXT NOT NULL,
                    to_node_id TEXT NOT NULL,
                    delivery_hash TEXT NULL,
                    quality INTEGER NULL,
                    efficiency INTEGER NULL,
                    cooperation INTEGER NULL,
                    outcome TEXT NOT NULL,
                    comment TEXT NULL,
                    issued_at INTEGER NOT NULL,
                    revision INTEGER NOT NULL DEFAULT 1,
                    signature TEXT NULL,
                    UNIQUE(from_node_id, to_node_id, task_id)
                );

                CREATE TABLE IF NOT EXISTS outbox (
                    outbox_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    event_id TEXT NOT NULL,
                    target_node_id TEXT NULL,
                    payload_json TEXT NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    next_retry_at INTEGER NOT NULL,
                    state TEXT NOT NULL DEFAULT 'pending',
                    created_at INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_outbox_due ON outbox(state, next_retry_at);
                """;
            command.ExecuteNonQuery();
            EnsureColumn(command, "peers", "icon", "TEXT NULL");
            EnsureColumn(command, "events", "sender_name", "TEXT NULL");
            EnsureColumn(command, "events", "sender_icon", "TEXT NULL");
            EnsureColumn(command, "sessions", "name", "TEXT NULL");
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertPeerAsync(PeerNode peer, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(command =>
        {
            command.CommandText = """
                INSERT INTO peers (node_id, display_name, icon, trust_state, public_key, profile_json, profile_version, endpoints_json, last_seen_at)
                VALUES ($id, $name, $icon, $trust, $key, $profile, $pver, $endpoints, $seen)
                ON CONFLICT(node_id) DO UPDATE SET
                    display_name = excluded.display_name,
                    icon = COALESCE(excluded.icon, icon),
                    trust_state = excluded.trust_state,
                    public_key = excluded.public_key,
                    profile_json = excluded.profile_json,
                    profile_version = excluded.profile_version,
                    endpoints_json = excluded.endpoints_json,
                    last_seen_at = excluded.last_seen_at;
                """;
            command.Parameters.AddWithValue("$id", peer.NodeId);
            command.Parameters.AddWithValue("$name", (object?)peer.DisplayName ?? DBNull.Value);
            command.Parameters.AddWithValue("$icon", (object?)peer.Icon ?? DBNull.Value);
            command.Parameters.AddWithValue("$trust", (int)peer.TrustState);
            command.Parameters.AddWithValue("$key", (object?)peer.PublicKeyBase64 ?? DBNull.Value);
            command.Parameters.AddWithValue("$profile", (object?)peer.ProfileJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$pver", peer.ProfileVersion);
            command.Parameters.AddWithValue("$endpoints", JsonSerializer.Serialize(peer.Endpoints));
            command.Parameters.AddWithValue("$seen", (object?)peer.LastSeenAt?.ToUnixTimeMilliseconds() ?? DBNull.Value);
            command.ExecuteNonQuery();
        }, cancellationToken);
    }

    public Task<PeerNode?> GetPeerAsync(string nodeId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command =>
        {
            command.CommandText = "SELECT * FROM peers WHERE node_id = $id;";
            command.Parameters.AddWithValue("$id", nodeId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPeer(reader) : null;
        }, cancellationToken);

    public async Task DeletePeerAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(command =>
        {
            command.CommandText = "DELETE FROM peers WHERE node_id = $id;";
            command.Parameters.AddWithValue("$id", nodeId);
            command.ExecuteNonQuery();
        }, cancellationToken);
    }

    public Task<List<PeerNode>> GetPeersAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(command =>
        {
            command.CommandText = "SELECT * FROM peers;";
            var peers = new List<PeerNode>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                peers.Add(ReadPeer(reader));
            }
            return peers;
        }, cancellationToken);

    public async Task SaveSessionAsync(A2ASession session, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(command =>
        {
            command.CommandText = """
                INSERT INTO sessions (session_id, kind, context_id, linked_task_id, created_by, coordinator_node_id, members_json, status, last_sequence, group_key_epoch, created_at, updated_at, name)
                VALUES ($id, $kind, $ctx, $task, $by, $coord, $members, $status, $seq, $epoch, $created, $updated, $name)
                ON CONFLICT(session_id) DO UPDATE SET
                    context_id = excluded.context_id,
                    linked_task_id = excluded.linked_task_id,
                    coordinator_node_id = excluded.coordinator_node_id,
                    members_json = excluded.members_json,
                    status = excluded.status,
                    last_sequence = excluded.last_sequence,
                    group_key_epoch = excluded.group_key_epoch,
                    updated_at = excluded.updated_at,
                    name = excluded.name;
                """;
            command.Parameters.AddWithValue("$id", session.SessionId);
            command.Parameters.AddWithValue("$kind", (int)session.Kind);
            command.Parameters.AddWithValue("$ctx", (object?)session.ContextId ?? DBNull.Value);
            command.Parameters.AddWithValue("$task", (object?)session.LinkedTaskId ?? DBNull.Value);
            command.Parameters.AddWithValue("$by", session.CreatedBy);
            command.Parameters.AddWithValue("$coord", (object?)session.CoordinatorNodeId ?? DBNull.Value);
            command.Parameters.AddWithValue("$members", JsonSerializer.Serialize(session.Members));
            command.Parameters.AddWithValue("$status", (int)session.Status);
            command.Parameters.AddWithValue("$seq", session.LastSequence);
            command.Parameters.AddWithValue("$epoch", session.GroupKeyEpoch);
            command.Parameters.AddWithValue("$created", session.CreatedAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$updated", session.UpdatedAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$name", (object?)session.Name ?? DBNull.Value);
            command.ExecuteNonQuery();
        }, cancellationToken);
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command =>
        {
            command.CommandText = "DELETE FROM events WHERE session_id = $sid;";
            command.Parameters.AddWithValue("$sid", sessionId);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "DELETE FROM sessions WHERE session_id = $sid;";
            command.Parameters.AddWithValue("$sid", sessionId);
            command.ExecuteNonQuery();
        }, cancellationToken);

    public Task<A2ASession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command =>
        {
            command.CommandText = "SELECT * FROM sessions WHERE session_id = $id;";
            command.Parameters.AddWithValue("$id", sessionId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadSession(reader) : null;
        }, cancellationToken);

    public Task<List<A2ASession>> GetSessionsAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(command =>
        {
            command.CommandText = "SELECT * FROM sessions ORDER BY updated_at DESC;";
            var sessions = new List<A2ASession>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) sessions.Add(ReadSession(reader));
            return sessions;
        }, cancellationToken);

    public Task<SessionEvent> AppendEventAsync(SessionEvent entry, bool assignLocalSequence = true, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command =>
        {
            command.CommandText = "SELECT event_id, session_id, sender_node_id, sender_name, sender_icon, timestamp, sequence, type, payload_json, prev_hash, signature FROM events WHERE event_id = $eid;";
            command.Parameters.AddWithValue("$eid", entry.EventId);
            using (var existing = command.ExecuteReader())
            {
                if (existing.Read())
                {
                    return ReadEvent(existing);
                }
            }

            command.Parameters.Clear();
            string? prevHash = null;
            long nextSequence;
            if (assignLocalSequence)
            {
                command.CommandText = """
                    SELECT e.sequence, e.event_id, e.session_id, e.sender_node_id, e.sender_name, e.sender_icon, e.timestamp, e.type, e.payload_json, e.prev_hash, e.signature
                    FROM events e WHERE e.session_id = $sid ORDER BY e.sequence DESC LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$sid", entry.SessionId);
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        nextSequence = reader.GetInt64(0) + 1;
                        prevHash = ReadEvent(reader).ComputeHash();
                    }
                    else
                    {
                        nextSequence = 1;
                    }
                }
            }
            else
            {
                if (entry.Sequence <= 0)
                {
                    throw new ArgumentException("Remote events must carry a positive sequence.");
                }
                nextSequence = entry.Sequence;
            }

            entry.Sequence = nextSequence;
            entry.PrevHash ??= prevHash;

            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO events (event_id, session_id, sender_node_id, sender_name, sender_icon, timestamp, sequence, type, payload_json, prev_hash, signature)
                VALUES ($eid, $sid, $sender, $sname, $sicon, $ts, $seq, $type, $payload, $prev, $sig);
                """;
            command.Parameters.AddWithValue("$eid", entry.EventId);
            command.Parameters.AddWithValue("$sid", entry.SessionId);
            command.Parameters.AddWithValue("$sender", entry.SenderNodeId);
            command.Parameters.AddWithValue("$sname", (object?)entry.SenderName ?? DBNull.Value);
            command.Parameters.AddWithValue("$sicon", (object?)entry.SenderIcon ?? DBNull.Value);
            command.Parameters.AddWithValue("$ts", entry.Timestamp.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$seq", entry.Sequence);
            command.Parameters.AddWithValue("$type", (int)entry.Type);
            command.Parameters.AddWithValue("$payload", (object?)entry.PayloadJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$prev", (object?)entry.PrevHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$sig", (object?)entry.Signature ?? DBNull.Value);
            try
            {
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                command.Parameters.Clear();
                command.CommandText = "SELECT event_id, session_id, sender_node_id, sender_name, sender_icon, timestamp, sequence, type, payload_json, prev_hash, signature FROM events WHERE event_id = $eid;";
                command.Parameters.AddWithValue("$eid", entry.EventId);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    return ReadEvent(reader);
                }
                throw;
            }

            if (assignLocalSequence)
            {
                command.Parameters.Clear();
                command.CommandText = "UPDATE sessions SET last_sequence = MAX(last_sequence, $seq), updated_at = $now WHERE session_id = $sid;";
                command.Parameters.AddWithValue("$seq", nextSequence);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$sid", entry.SessionId);
                command.ExecuteNonQuery();
            }
            return entry;
        }, cancellationToken);

    public Task<List<SessionEvent>> ReadEventsAsync(string sessionId, long afterSequence = 0, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command =>
        {
            command.CommandText = """
                SELECT event_id, session_id, sender_node_id, sender_name, sender_icon, timestamp, sequence, type, payload_json, prev_hash, signature
                FROM events WHERE session_id = $sid AND sequence > $after ORDER BY sequence ASC;
                """;
            command.Parameters.AddWithValue("$sid", sessionId);
            command.Parameters.AddWithValue("$after", afterSequence);
            var events = new List<SessionEvent>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                events.Add(ReadEvent(reader));
            }
            return events;
        }, cancellationToken);

    public async Task SaveTaskAsync(DistributedTask task, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(command =>
        {
            command.CommandText = """
                INSERT INTO tasks (task_id, group_id, coordinator_node_id, manifest_json, state, acceptances_json, deliveries_json, failure_reason, created_at, updated_at)
                VALUES ($id, $gid, $coord, $manifest, $state, $acc, $del, $reason, $created, $updated)
                ON CONFLICT(task_id) DO UPDATE SET
                    group_id = excluded.group_id,
                    manifest_json = excluded.manifest_json,
                    state = excluded.state,
                    acceptances_json = excluded.acceptances_json,
                    deliveries_json = excluded.deliveries_json,
                    failure_reason = excluded.failure_reason,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", task.TaskId);
            command.Parameters.AddWithValue("$gid", (object?)task.GroupId ?? DBNull.Value);
            command.Parameters.AddWithValue("$coord", task.CoordinatorNodeId);
            command.Parameters.AddWithValue("$manifest", task.Manifest.CanonicalJson());
            command.Parameters.AddWithValue("$state", (int)task.State);
            command.Parameters.AddWithValue("$acc", JsonSerializer.Serialize(task.Acceptances));
            command.Parameters.AddWithValue("$del", JsonSerializer.Serialize(task.Deliveries));
            command.Parameters.AddWithValue("$reason", (object?)task.FailureReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", task.CreatedAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$updated", task.UpdatedAt.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }, cancellationToken);
    }

    public Task<DistributedTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command =>
        {
            command.CommandText = "SELECT * FROM tasks WHERE task_id = $id;";
            command.Parameters.AddWithValue("$id", taskId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadTask(reader) : null;
        }, cancellationToken);

    public Task<List<DistributedTask>> GetTasksAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(command =>
        {
            command.CommandText = "SELECT * FROM tasks ORDER BY updated_at DESC;";
            var tasks = new List<DistributedTask>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) tasks.Add(ReadTask(reader));
            return tasks;
        }, cancellationToken);

    public Task<DistributedTask> TransitionTaskStateAsync(string taskId, DistributedTaskState next, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command =>
        {
            command.CommandText = "SELECT * FROM tasks WHERE task_id = $id;";
            command.Parameters.AddWithValue("$id", taskId);
            DistributedTask task;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    throw new KeyNotFoundException($"Task {taskId} not found.");
                }
                task = ReadTask(reader);
            }
            var previous = task.State;
            task.TransitionTo(next);
            command.Parameters.Clear();
            command.CommandText = "UPDATE tasks SET state = $state, updated_at = $updated WHERE task_id = $id AND state = $expected;";
            command.Parameters.AddWithValue("$state", (int)task.State);
            command.Parameters.AddWithValue("$updated", task.UpdatedAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$id", taskId);
            command.Parameters.AddWithValue("$expected", (int)previous);
            var updated = command.ExecuteNonQuery();
            if (updated == 0)
            {
                throw new InvalidOperationException($"Task {taskId} state changed concurrently.");
            }
            return task;
        }, cancellationToken);

    public Task<bool> SaveRatingAsync(RatingCredential rating, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command =>
        {
            rating.Scores.Validate();
            command.CommandText = """
                SELECT revision FROM ratings WHERE from_node_id = $from AND to_node_id = $to AND task_id = $task;
                """;
            command.Parameters.AddWithValue("$from", rating.FromNodeId);
            command.Parameters.AddWithValue("$to", rating.ToNodeId);
            command.Parameters.AddWithValue("$task", rating.TaskId);
            var existing = command.ExecuteScalar();
            if (existing is long existingRevision && existingRevision >= rating.Revision)
            {
                return false;
            }

            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO ratings (rating_id, task_id, from_node_id, to_node_id, delivery_hash, quality, efficiency, cooperation, outcome, comment, issued_at, revision, signature)
                VALUES ($rid, $task, $from, $to, $dhash, $q, $e, $c, $outcome, $comment, $issued, $rev, $sig)
                ON CONFLICT(from_node_id, to_node_id, task_id) DO UPDATE SET
                    rating_id = excluded.rating_id,
                    delivery_hash = excluded.delivery_hash,
                    quality = excluded.quality,
                    efficiency = excluded.efficiency,
                    cooperation = excluded.cooperation,
                    outcome = excluded.outcome,
                    comment = excluded.comment,
                    issued_at = excluded.issued_at,
                    revision = excluded.revision,
                    signature = excluded.signature;
                """;
            command.Parameters.AddWithValue("$rid", rating.RatingId);
            command.Parameters.AddWithValue("$task", rating.TaskId);
            command.Parameters.AddWithValue("$from", rating.FromNodeId);
            command.Parameters.AddWithValue("$to", rating.ToNodeId);
            command.Parameters.AddWithValue("$dhash", (object?)rating.DeliveryHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$q", (object?)rating.Scores.Quality ?? DBNull.Value);
            command.Parameters.AddWithValue("$e", (object?)rating.Scores.Efficiency ?? DBNull.Value);
            command.Parameters.AddWithValue("$c", (object?)rating.Scores.Cooperation ?? DBNull.Value);
            command.Parameters.AddWithValue("$outcome", rating.Outcome);
            command.Parameters.AddWithValue("$comment", (object?)rating.Comment ?? DBNull.Value);
            command.Parameters.AddWithValue("$issued", rating.IssuedAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$rev", rating.Revision);
            command.Parameters.AddWithValue("$sig", (object?)rating.Signature ?? DBNull.Value);
            command.ExecuteNonQuery();
            return true;
        }, cancellationToken);

    public Task<List<RatingCredential>> GetRatingsForAsync(string toNodeId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command =>
        {
            command.CommandText = "SELECT * FROM ratings WHERE to_node_id = $to ORDER BY issued_at ASC;";
            command.Parameters.AddWithValue("$to", toNodeId);
            var ratings = new List<RatingCredential>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ratings.Add(ReadRating(reader));
            }
            return ratings;
        }, cancellationToken);

    public Task<ReputationSummary> GetReputationSummaryAsync(string toNodeId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command =>
        {
            command.CommandText = """
                SELECT COUNT(*),
                       AVG(quality), AVG(efficiency), AVG(cooperation),
                       COUNT(DISTINCT from_node_id)
                FROM ratings WHERE to_node_id = $to AND outcome = 'accepted';
                """;
            command.Parameters.AddWithValue("$to", toNodeId);
            using var reader = command.ExecuteReader();
            var summary = new ReputationSummary();
            if (reader.Read())
            {
                summary.Rated = reader.GetInt32(0);
                summary.Quality = reader.IsDBNull(1) ? null : reader.GetDouble(1);
                summary.Efficiency = reader.IsDBNull(2) ? null : reader.GetDouble(2);
                summary.Cooperation = reader.IsDBNull(3) ? null : reader.GetDouble(3);
                summary.SourceCount = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
            }
            return summary;
        }, cancellationToken);

    public async Task<long> EnqueueOutboxAsync(string sessionId, string eventId, string? targetNodeId, string payloadJson, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(command =>
        {
            command.CommandText = """
                INSERT INTO outbox (session_id, event_id, target_node_id, payload_json, attempts, next_retry_at, state, created_at)
                VALUES ($sid, $eid, $target, $payload, 0, $next, 'pending', $now);
                SELECT last_insert_rowid();
                """;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            command.Parameters.AddWithValue("$sid", sessionId);
            command.Parameters.AddWithValue("$eid", eventId);
            command.Parameters.AddWithValue("$target", (object?)targetNodeId ?? DBNull.Value);
            command.Parameters.AddWithValue("$payload", payloadJson);
            command.Parameters.AddWithValue("$next", now);
            command.Parameters.AddWithValue("$now", now);
            return (long)(command.ExecuteScalar() ?? 0L);
        }, cancellationToken);
    }

    public Task<List<OutboxEntry>> DequeueDueOutboxAsync(DateTimeOffset now, int maxCount = 50, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command =>
        {
            command.CommandText = """
                SELECT outbox_id, session_id, event_id, target_node_id, payload_json, attempts, next_retry_at, state, created_at
                FROM outbox WHERE state = 'pending' AND next_retry_at <= $now ORDER BY outbox_id LIMIT $max;
                """;
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$max", maxCount);
            var entries = new List<OutboxEntry>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new OutboxEntry
                {
                    OutboxId = reader.GetInt64(0),
                    SessionId = reader.GetString(1),
                    EventId = reader.GetString(2),
                    TargetNodeId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    PayloadJson = reader.GetString(4),
                    Attempts = reader.GetInt32(5),
                    NextRetryAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                    State = reader.GetString(7),
                    CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8))
                });
            }
            return entries;
        }, cancellationToken);

    public async Task MarkOutboxSentAsync(long outboxId, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(command =>
        {
            command.CommandText = "UPDATE outbox SET state = 'sent' WHERE outbox_id = $id;";
            command.Parameters.AddWithValue("$id", outboxId);
            command.ExecuteNonQuery();
        }, cancellationToken);
    }

    public async Task MarkOutboxRetryAsync(long outboxId, DateTimeOffset nextRetryAt, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(command =>
        {
            command.CommandText = "UPDATE outbox SET attempts = attempts + 1, next_retry_at = $next WHERE outbox_id = $id;";
            command.Parameters.AddWithValue("$next", nextRetryAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$id", outboxId);
            command.ExecuteNonQuery();
        }, cancellationToken);
    }

    public async Task MarkOutboxFailedAsync(long outboxId, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(command =>
        {
            command.CommandText = "UPDATE outbox SET state = 'failed', attempts = attempts + 1 WHERE outbox_id = $id;";
            command.Parameters.AddWithValue("$id", outboxId);
            command.ExecuteNonQuery();
        }, cancellationToken);
    }

    private static void EnsureColumn(SqliteCommand command, string table, string column, string definition)
    {
        command.Parameters.Clear();
        command.CommandText = $"PRAGMA table_info({table});";
        var exists = false;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (!exists)
        {
            command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
            command.ExecuteNonQuery();
        }
    }

    private async Task ExecuteAsync(Action<SqliteCommand> body, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            using var command = _connection!.CreateCommand();
            body(command);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<SqliteCommand, T> body, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            using var command = _connection!.CreateCommand();
            return body(command);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized || _connection is null)
        {
            throw new InvalidOperationException("A2AStore is not initialized. Call InitializeAsync first.");
        }
    }

    private static PeerNode ReadPeer(SqliteDataReader reader) => new()
    {
        NodeId = reader.GetString(reader.GetOrdinal("node_id")),
        DisplayName = reader.IsDBNull(reader.GetOrdinal("display_name")) ? null : reader.GetString(reader.GetOrdinal("display_name")),
        Icon = reader.IsDBNull(reader.GetOrdinal("icon")) ? null : reader.GetString(reader.GetOrdinal("icon")),
        TrustState = (PeerTrustState)reader.GetInt32(reader.GetOrdinal("trust_state")),
        PublicKeyBase64 = reader.IsDBNull(reader.GetOrdinal("public_key")) ? null : reader.GetString(reader.GetOrdinal("public_key")),
        ProfileJson = reader.IsDBNull(reader.GetOrdinal("profile_json")) ? null : reader.GetString(reader.GetOrdinal("profile_json")),
        ProfileVersion = reader.GetInt64(reader.GetOrdinal("profile_version")),
        Endpoints = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("endpoints_json"))) ?? new(),
        LastSeenAt = reader.IsDBNull(reader.GetOrdinal("last_seen_at")) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("last_seen_at")))
    };

    private static A2ASession ReadSession(SqliteDataReader reader) => new()
    {
        SessionId = reader.GetString(reader.GetOrdinal("session_id")),
        Kind = (SessionKind)reader.GetInt32(reader.GetOrdinal("kind")),
        ContextId = reader.IsDBNull(reader.GetOrdinal("context_id")) ? null : reader.GetString(reader.GetOrdinal("context_id")),
        LinkedTaskId = reader.IsDBNull(reader.GetOrdinal("linked_task_id")) ? null : reader.GetString(reader.GetOrdinal("linked_task_id")),
        CreatedBy = reader.GetString(reader.GetOrdinal("created_by")),
        CoordinatorNodeId = reader.IsDBNull(reader.GetOrdinal("coordinator_node_id")) ? null : reader.GetString(reader.GetOrdinal("coordinator_node_id")),
        Members = JsonSerializer.Deserialize<List<SessionMember>>(reader.GetString(reader.GetOrdinal("members_json"))) ?? new(),
        Status = (SessionStatus)reader.GetInt32(reader.GetOrdinal("status")),
        LastSequence = reader.GetInt64(reader.GetOrdinal("last_sequence")),
        GroupKeyEpoch = reader.GetInt64(reader.GetOrdinal("group_key_epoch")),
        Name = reader.IsDBNull(reader.GetOrdinal("name")) ? null : reader.GetString(reader.GetOrdinal("name")),
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("created_at"))),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("updated_at")))
    };

    private static SessionEvent ReadEvent(SqliteDataReader reader) => new()
    {
        EventId = reader.GetString(reader.GetOrdinal("event_id")),
        SessionId = reader.GetString(reader.GetOrdinal("session_id")),
        SenderNodeId = reader.GetString(reader.GetOrdinal("sender_node_id")),
        SenderName = reader.IsDBNull(reader.GetOrdinal("sender_name")) ? null : reader.GetString(reader.GetOrdinal("sender_name")),
        SenderIcon = reader.IsDBNull(reader.GetOrdinal("sender_icon")) ? null : reader.GetString(reader.GetOrdinal("sender_icon")),
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("timestamp"))),
        Sequence = reader.GetInt64(reader.GetOrdinal("sequence")),
        Type = (SessionEventType)reader.GetInt32(reader.GetOrdinal("type")),
        PayloadJson = reader.IsDBNull(reader.GetOrdinal("payload_json")) ? null : reader.GetString(reader.GetOrdinal("payload_json")),
        PrevHash = reader.IsDBNull(reader.GetOrdinal("prev_hash")) ? null : reader.GetString(reader.GetOrdinal("prev_hash")),
        Signature = reader.IsDBNull(reader.GetOrdinal("signature")) ? null : reader.GetString(reader.GetOrdinal("signature"))
    };

    private static DistributedTask ReadTask(SqliteDataReader reader)
    {
        var task = new DistributedTask
        {
            TaskId = reader.GetString(reader.GetOrdinal("task_id")),
            GroupId = reader.IsDBNull(reader.GetOrdinal("group_id")) ? null : reader.GetString(reader.GetOrdinal("group_id")),
            CoordinatorNodeId = reader.GetString(reader.GetOrdinal("coordinator_node_id")),
            Manifest = JsonSerializer.Deserialize<TaskManifest>(reader.GetString(reader.GetOrdinal("manifest_json"))) ?? new TaskManifest(),
            Acceptances = JsonSerializer.Deserialize<List<TaskAcceptance>>(reader.GetString(reader.GetOrdinal("acceptances_json"))) ?? new(),
            Deliveries = JsonSerializer.Deserialize<List<TaskDelivery>>(reader.GetString(reader.GetOrdinal("deliveries_json"))) ?? new(),
            FailureReason = reader.IsDBNull(reader.GetOrdinal("failure_reason")) ? null : reader.GetString(reader.GetOrdinal("failure_reason")),
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("created_at"))),
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("updated_at")))
        };
        task.ForceState((DistributedTaskState)reader.GetInt32(reader.GetOrdinal("state")));
        return task;
    }

    private static RatingCredential ReadRating(SqliteDataReader reader) => new()
    {
        RatingId = reader.GetString(reader.GetOrdinal("rating_id")),
        TaskId = reader.GetString(reader.GetOrdinal("task_id")),
        FromNodeId = reader.GetString(reader.GetOrdinal("from_node_id")),
        ToNodeId = reader.GetString(reader.GetOrdinal("to_node_id")),
        DeliveryHash = reader.IsDBNull(reader.GetOrdinal("delivery_hash")) ? null : reader.GetString(reader.GetOrdinal("delivery_hash")),
        Scores = new RatingScores
        {
            Quality = reader.IsDBNull(reader.GetOrdinal("quality")) ? null : reader.GetInt32(reader.GetOrdinal("quality")),
            Efficiency = reader.IsDBNull(reader.GetOrdinal("efficiency")) ? null : reader.GetInt32(reader.GetOrdinal("efficiency")),
            Cooperation = reader.IsDBNull(reader.GetOrdinal("cooperation")) ? null : reader.GetInt32(reader.GetOrdinal("cooperation"))
        },
        Outcome = reader.GetString(reader.GetOrdinal("outcome")),
        Comment = reader.IsDBNull(reader.GetOrdinal("comment")) ? null : reader.GetString(reader.GetOrdinal("comment")),
        IssuedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("issued_at"))),
        Revision = reader.GetInt32(reader.GetOrdinal("revision")),
        Signature = reader.IsDBNull(reader.GetOrdinal("signature")) ? null : reader.GetString(reader.GetOrdinal("signature"))
    };

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

public sealed class OutboxEntry
{
    public long OutboxId { get; init; }
    public required string SessionId { get; init; }
    public required string EventId { get; init; }
    public string? TargetNodeId { get; init; }
    public required string PayloadJson { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset NextRetryAt { get; init; }
    public required string State { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
