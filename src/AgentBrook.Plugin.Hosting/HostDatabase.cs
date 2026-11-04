using AgentBrook.Plugin.Hosting.Abstractions;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Plugin.Hosting;

/// <summary>宿主侧运行时异常（错误码复用 ImErrorCodes 语义）。</summary>
public sealed class HostPipelineException(string errorCode, string message)
    : InvalidOperationException($"[{errorCode}] {message}")
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// 宿主持久化库（与 IM 领域库同一实例名 im.db 的宿主命名空间表——单物理库、逻辑所有者分表，§3.3）。
/// 表：host_plugin_state（命名空间 KV）、host_secrets（凭据引用）。
/// </summary>
public sealed class HostDatabase : IAsyncDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

    public HostDatabase(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        var dbPath = Path.Combine(rootPath, "im.db");
        _connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Pooling = false,
                DefaultTimeout = 15,   // 多连接（宿主/登录/设置页各一）并发时等锁，不立即失败
            }.ToString());
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS host_plugin_state (
              ns TEXT NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL,
              PRIMARY KEY (ns, key));
            CREATE TABLE IF NOT EXISTS host_secrets (
              ns TEXT NOT NULL, name TEXT NOT NULL, value TEXT NOT NULL,
              PRIMARY KEY (ns, name));
            """;
        cmd.ExecuteNonQuery();
    }

    public Microsoft.Data.Sqlite.SqliteConnection Connection => _connection;

    /// <summary>
    /// 串行门：SqliteConnection **非线程安全**（并发命令会破坏内部命令表，抛
    /// "Index was out of range ... RemoveCommand"）。宿主多通道（Telegram/微信/登录）并发取凭据时必然踩中，
    /// 故所有对该连接的访问都必须在 <see cref="Gate"/> 内完成。
    /// </summary>
    public object Gate { get; } = new();

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (Gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 在串行门内执行一次查询（**推荐用法**）：命令的创建、读取与释放都在锁内，
    /// 避免"锁内建命令、锁外读"导致的并发崩溃。
    /// </summary>
    public T WithCommand<T>(string sql, Func<Microsoft.Data.Sqlite.SqliteCommand, T> body,
        params (string Name, object? Value)[] parameters)
    {
        lock (Gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return body(cmd);
        }
    }

    /// <summary>⚠ 直接取命令：仅在调用方已持有 <see cref="Gate"/> 或单线程场景使用（遗留用法）。</summary>
    public Microsoft.Data.Sqlite.SqliteCommand CreateCommand(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (Gate)
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return cmd;
        }
    }

    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 活动租约记录（四标识分离 §15.4）：停启/更新切换签发新 ActivationId；
/// 旧句柄经 <see cref="ActivationRegistry.IsAlive"/> 全部失效。
/// </summary>
public sealed class ActivationRegistry
{
    private readonly object _gate = new();
    private readonly HashSet<string> _alive = [];

    public ActivationId Issue()
    {
        var id = ActivationId.New();
        lock (_gate) _alive.Add(id.Value);
        return id;
    }

    public void Revoke(ActivationId id)
    {
        lock (_gate) _alive.Remove(id.Value);
    }

    public bool IsAlive(ActivationId id)
    {
        lock (_gate) return _alive.Contains(id.Value);
    }
}
