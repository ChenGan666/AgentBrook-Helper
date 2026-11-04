namespace AgentBrook.Agent.Infrastructure;

/// <summary>
/// 会话持久化分流钩子（S7 §5-S7 任务3：工作区级存储模式）。
/// null / IsVersioned=false → BrookAgent 走 legacy 直写路径（行为不变）；
/// versioned 工作区（IM 试点）→ 快照经 CURRENT 提交单元推进（CAS 校验 RevisionConflict 上抛），
/// 同一会话不存在旧直写与新 CURRENT 并行——模式由工作区决定，不在飞回合期间切换。
/// 实现位于 AgentBrook.Agent.IM（VersionedSessionStore）。
/// </summary>
public interface ISessionCommitSink
{
    bool IsVersioned { get; }

    /// <summary>把当前序列化快照经提交协议推进（版本目录+CURRENT，CAS expectedRevision）。</summary>
    Task CommitSnapshotAsync(string sessionId, string snapshotJson);

    /// <summary>读 CURRENT 指向版本的 payload；会话尚无 CURRENT → null（调用方走 CreateSession）。</summary>
    Task<string?> LoadCurrentSnapshotAsync(string sessionId);
}
