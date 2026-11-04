namespace AgentBrook.Runtime;

/// <summary>
/// 删除协议（设计 §11.6 v4.3 写实持久化顺序——删除意图先于删目录落库）：
/// 1. 事务持久化删除意图：tombstone=Deleting，旧 generation 同步封版（此后提交校验直接拒绝）★依据先落库
/// 2. 取消/终结关联任务（S2：未提交 Running 回合取消；CommitPending 交恢复对账分类；
///    S4 扩展：Pending inbox→Cancelled、未发送 Outbox→Cancelled+远端取消意图、审批取消、下载授权撤销）
/// 3. 等待/取消在飞回合（租约持有者响应取消；晚到写入被 CAS/封版拒绝）
/// 4. 解绑路由（S4/S7）→ 删除会话目录
/// 5. 标记删除完成（Deleted；重启仍有效）
/// 恢复语义：Deleting/Deleted 都阻止旧提交与创建意图恢复；未完成删除继续完成；
/// 「取消待确认」记录不随目录删除（S4）。
/// </summary>
public sealed class DeleteProtocol(ImDatabase db, SessionFileSystem fs, CommitJournal journal)
{
    /// <summary>执行删除。onCancellingInFlight：协调器注入的在飞取消回调（步骤 2/3，可空）。</summary>
    public async Task DeleteSessionAsync(string sessionId, Func<Task>? waitForInFlightAsync = null)
    {
        var generation = fs.ReadCurrent(sessionId)?.Meta.Generation
            ?? ReadTombstoneGeneration(sessionId)
            ?? 1;

        // ── 步骤 1：删除意图先落库 + 封版 ──
        ImFaultPoints.Raise(ImFaultPoints.DeleteAfterIntent + ":before");
        await db.InTransactionAsync(tx =>
        {
            tx.Execute("""
                INSERT INTO tombstones(session_id, generation, state, created_at, updated_at)
                VALUES($id, $gen, 'Deleting', $at, $at)
                ON CONFLICT(session_id) DO UPDATE SET
                  state = 'Deleting', generation = excluded.generation, updated_at = excluded.updated_at
                """,
                ("$id", sessionId), ("$gen", generation), ("$at", DateTimeOffset.UtcNow.ToString("O")));
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        ImFaultPoints.Raise(ImFaultPoints.DeleteAfterIntent);

        // ── 步骤 2：取消未提交回合（CommitPending 留给恢复对账——删除优先于补提交）──
        var running = await journal.ListTurnsByStatusAsync("Running").ConfigureAwait(false);
        foreach (var turn in running.Where(t => t.SessionId == sessionId))
            await journal.CancelTurnAsync(turn.TurnId, "session deleting").ConfigureAwait(false);

        // ── 步骤 3：等待在飞回合退出（租约持有者响应取消）──
        if (waitForInFlightAsync is not null) await waitForInFlightAsync().ConfigureAwait(false);

        // ── 步骤 4：删除会话目录 ──
        fs.DeleteSessionDirectory(sessionId);
        ImFaultPoints.Raise(ImFaultPoints.DeleteAfterDirRemoval);

        // ── 步骤 5：标记删除完成 ──
        await db.InTransactionAsync(tx =>
        {
            tx.Execute("UPDATE tombstones SET state = 'Deleted', updated_at = $at WHERE session_id = $id",
                ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$id", sessionId));
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>恢复：未完成删除继续完成（从持久化状态继续，不恢复成正常会话）。</summary>
    public async Task ContinuePendingDeletionAsync(string sessionId)
    {
        var state = ReadTombstoneState(sessionId);
        if (state != "Deleting") return;

        // CommitPending 的提交在恢复对账里已被删除前置校验取消（旧 generation 不复活）
        await journal.CancelPendingCommitsAsync(sessionId, "session deleted (deleting)").ConfigureAwait(false);

        var running = await journal.ListTurnsByStatusAsync("Running", "CommitPending", "RecoveryRequired").ConfigureAwait(false);
        foreach (var turn in running.Where(t => t.SessionId == sessionId))
            await journal.CancelTurnAsync(turn.TurnId, "session deleted").ConfigureAwait(false);

        // 目录可能已删（F19）或未删（F18）——两种都直接继续完成
        fs.DeleteSessionDirectory(sessionId);
        ImFaultPoints.Raise(ImFaultPoints.DeleteAfterDirRemoval);

        await db.InTransactionAsync(tx =>
        {
            tx.Execute("UPDATE tombstones SET state = 'Deleted', updated_at = $at WHERE session_id = $id",
                ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$id", sessionId));
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    public string? ReadTombstoneState(string sessionId)
    {
        using var cmd = db.CreateCommand("SELECT state FROM tombstones WHERE session_id = $id", ("$id", sessionId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? reader.GetString(0) : null;
    }

    private long? ReadTombstoneGeneration(string sessionId)
    {
        using var cmd = db.CreateCommand("SELECT generation FROM tombstones WHERE session_id = $id", ("$id", sessionId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? reader.GetInt64(0) : null;
    }
}
