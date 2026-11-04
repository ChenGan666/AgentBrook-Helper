using System.Text;
using System.Text.Json;
using AgentBrook.Agent.Infrastructure;
using AgentBrook.Runtime;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Agent.IM;

/// <summary>工作区存储模式（§5-S7 任务3：工作区级模式，不在飞回合期间切换）。</summary>
public enum WorkspaceStorageMode
{
    /// <summary>旧直写路径（默认；未批准迁移的工作区保持 legacy）。</summary>
    Legacy = 0,
    /// <summary>版本化提交单元（versions/&lt;rev&gt;/ + CURRENT；IM 试点工作区）。</summary>
    Versioned = 1,
}

/// <summary>模式判定：versioned.flag 标记文件存在即 versioned（显式构造可覆盖；测试用）。</summary>
public static class WorkspaceStorageModeDetector
{
    public const string FlagFileName = "versioned.flag";

    public static WorkspaceStorageMode Detect(string workspaceRoot) =>
        File.Exists(Path.Combine(workspaceRoot, "im", FlagFileName))
            ? WorkspaceStorageMode.Versioned
            : WorkspaceStorageMode.Legacy;
}

/// <summary>
/// versioned 工作区的会话持久化（ISessionCommitSink 实现，注入 BrookAgent._persistence）：
/// 快照经 S2 提交单元（versions/&lt;rev&gt;/ + CURRENT + 提交日志）推进；本地/IM 写入者均过协调器租约（同会话单写）；
/// legacy 会话文件惰性迁移（首次 versioned 提交前：session.json 原样 → versions/1 + CURRENT=1，幂等）。
/// </summary>
public sealed class VersionedSessionStore : ISessionCommitSink
{
    private readonly SessionFileSystem _fs;
    private readonly CommitJournal _journal;
    private readonly SessionExecutionCoordinator _coordinator;
    private readonly Lock _revGate = new();
    private readonly Dictionary<string, long> _loadedRevision = [];   // 载入时 revision（CAS 依据——陈旧写者在此被拒）

    /// <summary>提交日志/文件系统（测试与恢复场景直接访问）。</summary>
    public CommitJournal Journal => _journal;
    public SessionFileSystem FileSystem => _fs;

    public VersionedSessionStore(ImDatabase db, string workspaceRoot)
    {
        _fs = new SessionFileSystem(workspaceRoot);
        _journal = new CommitJournal(db, _fs);
        _coordinator = new SessionExecutionCoordinator(_fs);
        Recovery = new RecoveryService(db, _fs, _journal, new DeleteProtocol(db, _fs, _journal));
    }

    /// <summary>共享核心的写者实例：各自持有 CAS 基线（载入时 revision），互不覆盖；
    /// 会话互斥由共享协调器保证。I02 双写者与真实 本地/IM 双入口 形态一致。</summary>
    public VersionedSessionStore(SessionFileSystem fs, CommitJournal journal,
        SessionExecutionCoordinator coordinator, RecoveryService recovery)
    {
        _fs = fs;
        _journal = journal;
        _coordinator = coordinator;
        Recovery = recovery;
    }

    /// <summary>启动恢复对账入口（宿主启动时调用；F08/F09 补切/补投影）。</summary>
    public RecoveryService Recovery { get; }

    public bool IsVersioned => true;

    public SessionExecutionCoordinator Coordinator => _coordinator;

    // ── 惰性迁移（I05）──

    /// <summary>
    /// 确保会话进入 versioned 形态：无 CURRENT 且 legacy session.json 存在 → 原样迁移为 versions/1+CURRENT=1；
    /// 无 CURRENT 且无 legacy → versions/1 = 空对象快照（首次会话初始化）。重复调用幂等。
    /// </summary>
    public void EnsureVersioned(string sessionId, string legacySessionFile)
    {
        if (_fs.ReadCurrent(sessionId) is not null)
        {
            RecordLoadedRevision(sessionId, _fs.ReadCurrent(sessionId)!.Value.Revision);
            return;   // 已 versioned——幂等（刷新载入 revision）
        }
        byte[] payload;
        if (File.Exists(legacySessionFile))
        {
            payload = File.ReadAllBytes(legacySessionFile);   // 原样迁移：旧格式内容不做变换
        }
        else
        {
            payload = Encoding.UTF8.GetBytes("{}");
        }
        var generation = NextGeneration(sessionId);
        _fs.WriteVersion(sessionId, generation, 1, $"c-legacy-{sessionId}", payload);
        _fs.SwitchCurrent(sessionId, 1);
        RecordLoadedRevision(sessionId, 1);
    }

    /// <summary>种子化新会话首版快照（真实 AIAgent 生成的 payload；无 CURRENT 时建 versions/1+CURRENT=1）。</summary>
    public void SeedSession(string sessionId, byte[] payload)
    {
        if (_fs.ReadCurrent(sessionId) is not null) return;   // 幂等
        var generation = NextGeneration(sessionId);
        _fs.WriteVersion(sessionId, generation, 1, $"c-seed-{sessionId}", payload);
        _fs.SwitchCurrent(sessionId, 1);
        RecordLoadedRevision(sessionId, 1);
    }

    private void RecordLoadedRevision(string sessionId, long revision)
    {
        lock (_revGate) _loadedRevision[sessionId] = revision;
    }

    private long LoadedRevision(string sessionId)
    {
        lock (_revGate)
        {
            if (_loadedRevision.TryGetValue(sessionId, out var rev)) return rev;
        }
        var current = _fs.ReadCurrent(sessionId)?.Revision
            ?? throw new RuntimePipelineException(ImErrorCodes.GenerationStale,
                $"会话 {sessionId} 无载入记录且无 CURRENT——先载入再提交。");
        return current;
    }

    private long NextGeneration(string sessionId)
    {
        // 删除重建 → tombstone generation + 1；否则 1（ tombstone 查询在 journal 内）
        var gen = 1L;
        using var cmd = _journal.Db.CreateCommand(
            "SELECT generation FROM tombstones WHERE session_id = $id", ("$id", sessionId));
        var reader = cmd.ExecuteReader();
        if (reader.Read()) gen = reader.GetInt64(0) + 1;
        return gen;
    }

    // ── ISessionCommitSink（BrookAgent 本地保存路径）──

    public async Task CommitSnapshotAsync(string sessionId, string snapshotJson)
    {
        // CAS 依据 = 本写入者【载入时】的 revision（不是租约当前值）：
        // 载入后若他人推进了 CURRENT，本提交在 CommitPendingAsync 处 RevisionConflict 拒绝——
        // 陈旧内存快照不得覆盖新提交（§5-S7 I02 核心语义）。
        // 先查 CURRENT：legacy 创建的会话（如"新对话"、删除后新建）从未走版本化载入——
        // 无内存基线也无 CURRENT 时以本次快照初始化 rev 1（EnsureVersioned 语义落地，SeedSession 同形态）；
        // 墓碑会话（已删除=归档）跳过持久化不复活。LoadedRevision 必须在 CURRENT 确认之后取（否则未初始化会话直接抛）。
        var current = _fs.ReadCurrent(sessionId);
        if (current is null)
        {
            using var tb = _journal.Db.CreateCommand(
                "SELECT generation FROM tombstones WHERE session_id = $id", ("$id", sessionId));
            var tombstoned = tb.ExecuteScalar() is not null;
            if (tombstoned)
            {
                Console.Error.WriteLine($"[im] 跳过已删除会话的持久化：{sessionId}");
                return;
            }
            var initGeneration = NextGeneration(sessionId);
            _fs.WriteVersion(sessionId, initGeneration, 1, $"c-init-{sessionId}", Encoding.UTF8.GetBytes(snapshotJson));
            _fs.SwitchCurrent(sessionId, 1);
            RecordLoadedRevision(sessionId, 1);
            return;
        }
        var expectedRevision = LoadedRevision(sessionId);
        var generation = current.Value.Meta.Generation;
        await using var lease = await _coordinator.AcquireAsync(sessionId).ConfigureAwait(false);
        var ctx = new TurnExecutionContext(
            sessionId, generation, expectedRevision,
            "t-local-" + Guid.NewGuid().ToString("N")[..12], TurnOrigin.Local,
            null, null, PermissionPolicy.P0Default, new NullApprovalPort());
        await _journal.BeginTurnAsync(ctx).ConfigureAwait(false);
        await _journal.CommitPendingAsync(
            ctx,
            events: [],   // 本地快照推进：无新转写事件（转写显示由 BrookAgent transcript 管理）
            snapshotPayload: Encoding.UTF8.GetBytes(snapshotJson),
            resultText: null).ConfigureAwait(false);
        RecordLoadedRevision(sessionId, expectedRevision + 1);   // 本写者的载入版本随提交推进
    }

    public async Task<string?> LoadCurrentSnapshotAsync(string sessionId)
    {
        var current = _fs.ReadCurrent(sessionId);
        if (current is null) return null;
        var payload = _fs.ReadSnapshot(sessionId, current.Value.Revision);
        RecordLoadedRevision(sessionId, current.Value.Revision);   // 载入点 = 本写者 CAS 基准
        return Encoding.UTF8.GetString(payload);
    }

    /// <summary>当前版本（本地/IM 写入者的陈旧检测输入）。</summary>
    public SessionVersion ReadVersion(string sessionId) =>
        _coordinator.ReadCurrentVersion(sessionId);

    /// <summary>版本清单（迁移幂等断言用）。</summary>
    public IReadOnlyList<long> ListVersions(string sessionId) => _fs.ListVersions(sessionId);

    /// <summary>读 CURRENT payload 字节（执行器/测试载入用）。</summary>
    public byte[] ReadCurrentPayload(string sessionId)
    {
        var current = _fs.ReadCurrent(sessionId)
            ?? throw new RuntimePipelineException(ImErrorCodes.GenerationStale, $"会话 {sessionId} 无 CURRENT。");
        RecordLoadedRevision(sessionId, current.Revision);   // 读即载入（执行器 CAS 基准）
        return _fs.ReadSnapshot(sessionId, current.Revision);
    }

    private sealed class NullApprovalPort : IApprovalPort
    {
        public Task<AgentBrook.Runtime.Abstractions.ApprovalDecision> RequestAsync(
            ApprovalRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(AgentBrook.Runtime.Abstractions.ApprovalDecision.Approved);
    }
}
