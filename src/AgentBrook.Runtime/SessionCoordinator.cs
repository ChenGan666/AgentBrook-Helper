using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Runtime;

/// <summary>租约交付物：会话当前版本（读 CURRENT meta 现值）；执行体发现内存快照落后必须重载（设计 §6.2）。</summary>
public sealed record SessionLeaseGrant(string SessionId, long Generation, long Revision);

/// <summary>
/// 会话执行协调器（ADR-D1/D3）：租约范围 = 读最新 → 执行 → 提交；
/// 同 sessionId 并行写入执行者峰值 = 1（§6.3 可量化判定）。
/// Generation/Revision 校验在提交协议 CAS 兜底（CommitJournal.CommitPendingAsync）。
/// </summary>
public sealed class SessionExecutionCoordinator(SessionFileSystem fs)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SemaphoreSlim> _leases = new();
    private readonly Dictionary<string, int> _holders = new();

    /// <summary>取得租约（会话级互斥）。取消/超时由调用方控制（UI Queued 语义在 S7/S8 呈现）。</summary>
    public async Task<SessionLease> AcquireAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var semaphore = GetOrCreateSemaphore(sessionId);
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate) _holders[sessionId] = _holders.GetValueOrDefault(sessionId) + 1;

        // 交付现值（无会话目录 → 初始版本 g1/r0，由调用方初始化）
        var version = fs.ReadCurrent(sessionId) is { } cur
            ? new SessionLeaseGrant(sessionId, cur.Meta.Generation, cur.Revision)
            : new SessionLeaseGrant(sessionId, 1, 0);
        return new SessionLease(this, sessionId, semaphore, version);
    }

    private SemaphoreSlim GetOrCreateSemaphore(string sessionId)
    {
        lock (_gate)
        {
            if (!_leases.TryGetValue(sessionId, out var s))
            {
                s = new SemaphoreSlim(1, 1);
                _leases[sessionId] = s;
            }
            return s;
        }
    }

    internal void Release(string sessionId, SemaphoreSlim semaphore)
    {
        lock (_gate)
        {
            var left = _holders.GetValueOrDefault(sessionId) - 1;
            if (left <= 0) _holders.Remove(sessionId);
            else _holders[sessionId] = left;
        }
        semaphore.Release();
    }

    /// <summary>会话当前版本（租约内重载判定用）。</summary>
    public SessionVersion ReadCurrentVersion(string sessionId) =>
        fs.ReadCurrent(sessionId) is { } cur ? new SessionVersion(cur.Meta.Generation, cur.Revision) : SessionVersion.Initial;
}

/// <summary>持有中的会话租约：交付版本 + 释放。离开会话时 revision 已被外部推进 → 丢弃内存状态不写回（§6.3）。</summary>
public sealed class SessionLease(
    SessionExecutionCoordinator owner,
    string sessionId,
    SemaphoreSlim semaphore,
    SessionLeaseGrant grant) : IAsyncDisposable
{
    public string SessionId { get; } = sessionId;
    public SessionLeaseGrant Grant { get; } = grant;

    /// <summary>内存快照是否已落后（落后必须重载后才可执行）。</summary>
    public bool IsStale(long inMemoryRevision) => inMemoryRevision != Grant.Revision;

    public ValueTask DisposeAsync()
    {
        owner.Release(SessionId, semaphore);
        return ValueTask.CompletedTask;
    }
}
