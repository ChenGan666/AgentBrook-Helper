using Microsoft.Data.Sqlite;

namespace AgentBrook.Runtime;

/// <summary>恢复/事务相关领域异常（ErrorCategory 用 ImErrorCodes 语义）。</summary>
public sealed class RuntimePipelineException(string errorCode, string message, Exception? inner = null)
    : InvalidOperationException($"[{errorCode}] {message}", inner)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// im.db 访问单元（内部实现，不出契约——设计 §15.1"内部实现不入公共契约"）。
/// 事务边界（§3.3 跨层事务约束）：提交日志/事件与 Messaging 表共享同一物理事务；
/// Messaging 经 <see cref="ImTransaction"/> 受控参与，禁止跨数据库伪原子。
/// </summary>
public sealed class ImDatabase : IAsyncDisposable
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _connection;

    private ImDatabase(SqliteConnection connection, string rootPath)
    {
        _connection = connection;
        RootPath = rootPath;
    }

    /// <summary>数据根（工作区级目录；im.db 位于 &lt;root&gt;/im.db）。</summary>
    public string RootPath { get; }

    /// <summary>
    /// 连接级串行门（Microsoft.Data.Sqlite 连接非线程安全）：所有并发入口
    /// （Sink/调度泵/投递泵/恢复）的 DB 触碰区间须持门；耗时外部调用（模型/网络）不持门。
    /// </summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public static async Task<ImDatabase> OpenAsync(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        var dbPath = Path.Combine(rootPath, "im.db");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false, // 测试与故障注入要求独占、确定性的文件句柄
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
            pragma.ExecuteNonQuery();
        }

        var db = new ImDatabase(connection, rootPath);
        db.EnsureSchema();
        return db;
    }

    public SqliteConnection Connection => _connection;

    private void EnsureSchema()
    {
        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_meta (
              key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT OR IGNORE INTO schema_meta(key, value) VALUES('schema_version', '1');

            -- 回合（设计 §11.4：hasUnsafeAttempt 写前标记）
            CREATE TABLE IF NOT EXISTS turns (
              turn_id TEXT PRIMARY KEY,
              session_id TEXT NOT NULL,
              generation INTEGER NOT NULL,
              expected_revision INTEGER NOT NULL,
              origin TEXT NOT NULL,
              status TEXT NOT NULL,              -- Running | CommitPending | Completed | Failed | Cancelled | RecoveryRequired
              has_unsafe_attempt INTEGER NOT NULL DEFAULT 0,
              unsafe_detail TEXT,
              reply_target_json TEXT,
              created_at TEXT NOT NULL,
              completed_at TEXT);
            CREATE INDEX IF NOT EXISTS ix_turns_session ON turns(session_id, status);

            -- 提交（Pending=CommitPending 落库待切 CURRENT；Committed=步骤5账务完成）
            CREATE TABLE IF NOT EXISTS commits (
              commit_id TEXT PRIMARY KEY,
              turn_id TEXT NOT NULL,
              session_id TEXT NOT NULL,
              generation INTEGER NOT NULL,
              revision INTEGER NOT NULL,
              state TEXT NOT NULL,               -- Pending | Committed | Cancelled
              snapshot_dir TEXT NOT NULL,
              result_text TEXT,
              outbox_plan_json TEXT,
              created_at TEXT NOT NULL,
              committed_at TEXT);
            CREATE INDEX IF NOT EXISTS ix_commits_session ON commits(session_id, state);

            -- 权威转写事件批次（投影的唯一依据；(turn_id,event_index) 唯一约束）
            CREATE TABLE IF NOT EXISTS turn_events (
              turn_id TEXT NOT NULL,
              event_index INTEGER NOT NULL,
              role TEXT NOT NULL,
              text TEXT NOT NULL,
              artifacts_json TEXT,
              PRIMARY KEY (turn_id, event_index));

            -- 投影水位（优化；恢复以文件扫描为准）
            CREATE TABLE IF NOT EXISTS projections (
              turn_id TEXT PRIMARY KEY,
              projected_index INTEGER NOT NULL);

            -- 删除/墓碑（§11.6：Deleting 封版 → Deleted；generation 计数入 tombstone）
            CREATE TABLE IF NOT EXISTS tombstones (
              session_id TEXT PRIMARY KEY,
              generation INTEGER NOT NULL,
              state TEXT NOT NULL,               -- Deleting | Deleted
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL);

            -- Blob（设计 §8：生成 ID 落盘；namespace+purpose 授权；引用计数）
            CREATE TABLE IF NOT EXISTS blobs (
              blob_id TEXT PRIMARY KEY,
              namespace TEXT NOT NULL,
              purpose TEXT NOT NULL,
              sha256 TEXT NOT NULL,
              length INTEGER NOT NULL,
              file_name TEXT,
              media_type TEXT,
              ref_count INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL);

            -- 投递键唯一约束在 S4 的 inbox/outbox 建表时补充（同库同事务边界）
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>在单一物理事务内执行（异常即回滚并上抛）。</summary>
    public async Task<T> InTransactionAsync<T>(Func<ImTransaction, Task<T>> work)
    {
        await using var tx = _connection.BeginTransaction();
        var unit = new ImTransaction(_connection, tx);
        try
        {
            var result = await work(unit).ConfigureAwait(false);
            tx.Commit();
            return result;
        }
        catch
        {
            try { await tx.RollbackAsync().ConfigureAwait(false); } catch { /* 回滚失败以原异常为准 */ }
            throw;
        }
    }

    public Task InTransactionAsync(Func<ImTransaction, Task> work) =>
        InTransactionAsync<object?>(async u => { await work(u).ConfigureAwait(false); return null; });

    /// <summary>非事务单命令（只读/PRAGMA 用；写入一律走事务）。Execute 为别名（与 ImTransaction 命名对齐）。</summary>
    public int ExecuteNonQuery(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public int Execute(string sql, params (string Name, object? Value)[] parameters) =>
        ExecuteNonQuery(sql, parameters);

    public SqliteCommand CreateCommand(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>单一物理事务内的受控参与接口（§3.3）：提交日志/事件与领域表共用，不暴露任意 SQL 句柄给插件层。</summary>
public sealed class ImTransaction(SqliteConnection connection, SqliteTransaction transaction)
{
    public SqliteTransaction Inner { get; } = transaction;

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = Inner;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public SqliteCommand CreateCommand(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = Inner;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
