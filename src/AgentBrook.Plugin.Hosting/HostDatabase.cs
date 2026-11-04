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
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
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

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public Microsoft.Data.Sqlite.SqliteCommand CreateCommand(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
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
