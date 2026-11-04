using Microsoft.Data.Sqlite;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Runtime;

/// <summary>提交回执（回合提交成功后交调用方/恢复对账用）。</summary>
public sealed record CommitReceipt(string CommitId, long Revision, string SessionId);

/// <summary>
/// 提交日志与回合状态机（设计 §11.5 提交协议，顺序禁止反向）：
/// 1. BeginTurn（turns 插入 Running）
/// 2. CommitPending——版本目录先写并定稿（meta 最后写=完成标记）→ 单一 SQLite 事务
///    （turn_events 批次 + commits Pending 行 + turn→CommitPending；事务成功才许切 CURRENT）
/// 3. 原子切换 CURRENT ★唯一判据
/// 4. 显示投影（幂等：尾部三态修复 + (turnId,ei) 补写）
/// 5. 事务：turn→Completed；commits→Committed；（S4 起：inbox→Done + outbox 创建经 participant 同事务）
/// 6. 释放租约/通知
/// </summary>
public sealed class CommitJournal(ImDatabase db, SessionFileSystem fs)
{
    public ImDatabase Db { get; } = db;
    public SessionFileSystem FileSystem { get; } = fs;

    // ── 回合生命周期 ──

    /// <summary>回合开始。删除封版（tombstone）即拒绝——删除意图先落库后，旧提交路径直接失效（§11.6）。</summary>
    public async Task<string> BeginTurnAsync(TurnExecutionContext context)
    {
        await EnsureSessionNotDeletedAsync(context.SessionId, context.Generation).ConfigureAwait(false);
        var turnId = context.TurnId;
        await Db.InTransactionAsync(tx =>
        {
            tx.Execute("""
                INSERT INTO turns(turn_id, session_id, generation, expected_revision, origin, status, has_unsafe_attempt, reply_target_json, created_at)
                VALUES($turn_id, $session_id, $generation, $expected_revision, $origin, 'Running', 0, $reply_target, $created_at)
                """,
                ("$turn_id", turnId), ("$session_id", context.SessionId),
                ("$generation", context.Generation), ("$expected_revision", context.ExpectedRevision),
                ("$origin", context.Origin.ToString()),
                ("$reply_target", context.ReplyTarget is null ? null : ImJson.Serialize(context.ReplyTarget)),
                ("$created_at", DateTimeOffset.UtcNow.ToString("O")));
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        return turnId;
    }

    /// <summary>非安全工具写前标记（§11.4）：UPDATE 成功（恰 1 行）才放行；失败 → 存储不可用，工具禁止执行。</summary>
    public Task<bool> MarkUnsafeAttemptAsync(string turnId, string detailJson)
    {
        var rows = Db.ExecuteNonQuery(
            """
            UPDATE turns SET has_unsafe_attempt = 1, unsafe_detail = $detail
            WHERE turn_id = $turn_id AND status = 'Running'
            """,
            ("$turn_id", turnId), ("$detail", detailJson));
        return Task.FromResult(rows == 1);
    }

    /// <summary>
    /// 提交协议步骤 2～5。participant：同事务参与接口（S4 Inbox Done/Outbox 创建；§3.3 禁止拆库伪原子）。
    /// </summary>
    public async Task<CommitReceipt> CommitPendingAsync(
        TurnExecutionContext context,
        IReadOnlyList<TurnEvent> events,
        byte[] snapshotPayload,
        string? resultText,
        string? outboxPlanJson = null,
        Func<ImTransaction, Task>? participant = null)
    {
        var (sessionId, generation, expectedRevision, turnId) =
            (context.SessionId, context.Generation, context.ExpectedRevision, context.TurnId);

        // 封版复验（撤权/删除后不得晚到写入）
        await EnsureSessionNotDeletedAsync(sessionId, generation).ConfigureAwait(false);

        // CAS：租约交付的 expectedRevision 必须仍是 CURRENT（陈旧写者必须重载，D03/D04）
        var current = FileSystem.ReadCurrent(sessionId)
            ?? throw new RuntimePipelineException(ImErrorCodes.GenerationStale,
                $"会话 {sessionId} 无 CURRENT（尚未初始化）。");
        if (current.Meta.Generation != generation)
            throw new RuntimePipelineException(ImErrorCodes.GenerationStale,
                $"generation 失配：持有 g{generation}，CURRENT g{current.Meta.Generation}。");
        if (current.Revision != expectedRevision)
            throw new RuntimePipelineException(ImErrorCodes.RevisionConflict,
                $"陈旧快照：持有 r{expectedRevision}，CURRENT r{current.Revision}。须重载后重试。");

        var commitId = "c-" + Guid.NewGuid().ToString("N");
        var newRevision = expectedRevision + 1;

        // ── 版本目录先写并定稿（meta 最后写 = 完成标记）──
        FileSystem.WriteVersion(sessionId, generation, newRevision, commitId, snapshotPayload);

        // ── 单一事务：events + commits(Pending) + turn→CommitPending（F07：内部中断全回滚）──
        ImFaultPoints.Raise(ImFaultPoints.CommitTxnBefore);
        await Db.InTransactionAsync(async tx =>
        {
            foreach (var e in events)
            {
                tx.Execute("""
                    INSERT INTO turn_events(turn_id, event_index, role, text, artifacts_json)
                    VALUES($turn_id, $ei, $role, $text, $artifacts)
                    """,
                    ("$turn_id", e.TurnId), ("$ei", e.EventIndex), ("$role", e.Role),
                    ("$text", e.Text), ("$artifacts", e.ArtifactsJson));
            }
            tx.Execute("""
                INSERT INTO commits(commit_id, turn_id, session_id, generation, revision, state, snapshot_dir, result_text, outbox_plan_json, created_at)
                VALUES($commit_id, $turn_id, $session_id, $generation, $revision, 'Pending', $dir, $result, $plan, $created_at)
                """,
                ("$commit_id", commitId), ("$turn_id", turnId), ("$session_id", sessionId),
                ("$generation", generation), ("$revision", newRevision),
                ("$dir", $"versions/{newRevision}"), ("$result", resultText), ("$plan", outboxPlanJson),
                ("$created_at", DateTimeOffset.UtcNow.ToString("O")));
            tx.Execute("UPDATE turns SET status = 'CommitPending' WHERE turn_id = $turn_id", ("$turn_id", turnId));
            ImFaultPoints.Raise(ImFaultPoints.CommitTxnInside);
            await Task.CompletedTask.ConfigureAwait(false);
        }).ConfigureAwait(false);

        // ── 步骤 3：原子切换 CURRENT（唯一判据）──
        FileSystem.SwitchCurrent(sessionId, newRevision);

        // ── 步骤 4：显示投影（幂等）+ 水位 ──
        ImFaultPoints.Raise(ImFaultPoints.ProjectionBefore);
        Projection.RepairIn(sessionId, FileSystem.SessionsRoot, events);
        await UpdateProjectionWatermarkAsync(events).ConfigureAwait(false);

        // ── 步骤 5：完成账务（F11：中断全回滚，重做账务不重做模型）──
        ImFaultPoints.Raise(ImFaultPoints.FinalizeTxnBefore);
        await Db.InTransactionAsync(async tx =>
        {
            tx.Execute("UPDATE turns SET status = 'Completed', completed_at = $at WHERE turn_id = $turn_id",
                ("$turn_id", turnId), ("$at", DateTimeOffset.UtcNow.ToString("O")));
            tx.Execute("UPDATE commits SET state = 'Committed', committed_at = $at WHERE commit_id = $commit_id",
                ("$commit_id", commitId), ("$at", DateTimeOffset.UtcNow.ToString("O")));
            if (participant is not null) await participant(tx).ConfigureAwait(false);
            ImFaultPoints.Raise(ImFaultPoints.FinalizeTxnInside);
            await Task.CompletedTask.ConfigureAwait(false);
        }).ConfigureAwait(false);

        return new CommitReceipt(commitId, newRevision, sessionId);
    }

    // ── 恢复对账操作（§11.5 表）──

    /// <summary>F08 恢复：补切 CURRENT 指向已完整版本，然后继续步骤 4-5。不重跑模型/工具。</summary>
    public async Task FinishPendingCommitAsync(CommitRow commit)
    {
        if (!FileSystem.IsVersionComplete(commit.SessionId, commit.Revision, commit.CommitId))
            throw new RuntimePipelineException(ImErrorCodes.RecoveryRequired,
                $"版本 {commit.SnapshotDir} 不完整，不能补切。");
        var current = FileSystem.ReadCurrent(commit.SessionId);
        if (current is not null && current.Value.Revision < commit.Revision)
            FileSystem.SwitchCurrent(commit.SessionId, commit.Revision);
        await ProjectAndFinalizeAsync(commit).ConfigureAwait(false);
    }

    /// <summary>CURRENT 已更晚且确认本提交已入历史：只补投影+账务，不动 CURRENT。</summary>
    public async Task FinalizeAccountingOnlyAsync(CommitRow commit)
    {
        await ProjectAndFinalizeAsync(commit, switchCurrent: false).ConfigureAwait(false);
    }

    private async Task ProjectAndFinalizeAsync(CommitRow commit, bool switchCurrent = true)
    {
        var events = await LoadTurnEventsAsync(commit.TurnId).ConfigureAwait(false);
        Projection.RepairIn(commit.SessionId, FileSystem.SessionsRoot, events);
        await UpdateProjectionWatermarkAsync(events).ConfigureAwait(false);

        await Db.InTransactionAsync(tx =>
        {
            tx.Execute("UPDATE turns SET status = 'Completed', completed_at = $at WHERE turn_id = $turn_id AND status != 'Cancelled'",
                ("$turn_id", commit.TurnId), ("$at", DateTimeOffset.UtcNow.ToString("O")));
            tx.Execute("UPDATE commits SET state = 'Committed', committed_at = $at WHERE commit_id = $commit_id",
                ("$commit_id", commit.CommitId), ("$at", DateTimeOffset.UtcNow.ToString("O")));
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        _ = switchCurrent;
    }

    /// <summary>把回合标记为需要人工恢复（未知副作用禁止自动重跑；D12）。</summary>
    public Task MarkRecoveryRequiredAsync(string turnId, string reason)
    {
        Db.ExecuteNonQuery(
            "UPDATE turns SET status = 'RecoveryRequired', unsafe_detail = COALESCE(unsafe_detail, '') || $reason WHERE turn_id = $turn_id",
            ("$reason", " | " + reason), ("$turn_id", turnId));
        return Task.CompletedTask;
    }

    /// <summary>只读回合安全重跑标记：Running 未提交且无非安全尝试 → Failed（安全重试类）。</summary>
    public Task MarkFailedSafeAsync(string turnId)
    {
        Db.ExecuteNonQuery(
            "UPDATE turns SET status = 'Failed', completed_at = $at WHERE turn_id = $turn_id AND has_unsafe_attempt = 0",
            ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$turn_id", turnId));
        return Task.CompletedTask;
    }

    /// <summary>取消回合（删除协议/撤权；CommitPending 的提交留给恢复对账分类，不在此掩盖）。</summary>
    public Task CancelTurnAsync(string turnId, string reason)
    {
        Db.ExecuteNonQuery(
            "UPDATE turns SET status = 'Cancelled', unsafe_detail = COALESCE(unsafe_detail, '') || $reason, completed_at = $at WHERE turn_id = $turn_id AND status IN ('Running')",
            ("$reason", " | cancelled: " + reason), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$turn_id", turnId));
        return Task.CompletedTask;
    }

    /// <summary>取消会话全部未提交 Pending commits（恢复对账：删除/撤权/换号优先于补提交）。</summary>
    public Task CancelPendingCommitsAsync(string sessionId, string reason)
    {
        Db.ExecuteNonQuery(
            "UPDATE commits SET state = 'Cancelled', result_text = COALESCE(result_text, '') || $reason WHERE session_id = $session AND state = 'Pending'",
            ("$reason", " | cancelled: " + reason), ("$session", sessionId));
        return Task.CompletedTask;
    }

    // ── 查询 ──

    public Task<TurnRow?> GetTurnAsync(string turnId)
    {
        using var cmd = Db.CreateCommand("SELECT * FROM turns WHERE turn_id = $id", ("$id", turnId));
        using var reader = cmd.ExecuteReader();
        return Task.FromResult(reader.Read() ? ReadTurn(reader) : null);
    }

    public Task<List<TurnRow>> ListTurnsByStatusAsync(params string[] statuses)
    {
        var inList = string.Join(",", statuses.Select((_, i) => $"$s{i}"));
        using var cmd = Db.CreateCommand($"SELECT * FROM turns WHERE status IN ({inList})");
        for (var i = 0; i < statuses.Length; i++) cmd.Parameters.AddWithValue($"$s{i}", statuses[i]);
        using var reader = cmd.ExecuteReader();
        var list = new List<TurnRow>();
        while (reader.Read()) list.Add(ReadTurn(reader));
        return Task.FromResult(list);
    }

    public Task<List<CommitRow>> ListCommitsAsync(string state)
    {
        using var cmd = Db.CreateCommand("SELECT * FROM commits WHERE state = $state", ("$state", state));
        using var reader = cmd.ExecuteReader();
        var list = new List<CommitRow>();
        while (reader.Read()) list.Add(ReadCommit(reader));
        return Task.FromResult(list);
    }

    public Task<CommitRow?> GetCommitAsync(string commitId)
    {
        using var cmd = Db.CreateCommand("SELECT * FROM commits WHERE commit_id = $id", ("$id", commitId));
        using var reader = cmd.ExecuteReader();
        return Task.FromResult(reader.Read() ? ReadCommit(reader) : null);
    }

    public Task<List<TurnEvent>> LoadTurnEventsAsync(string turnId)
    {
        using var cmd = Db.CreateCommand(
            "SELECT turn_id, event_index, role, text, artifacts_json FROM turn_events WHERE turn_id = $id ORDER BY event_index",
            ("$id", turnId));
        using var reader = cmd.ExecuteReader();
        var list = new List<TurnEvent>();
        while (reader.Read())
            list.Add(new TurnEvent(
                reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        return Task.FromResult(list);
    }

    public Task<bool> TombstoneBlocksAsync(string sessionId, long generation)
    {
        using var cmd = Db.CreateCommand(
            "SELECT generation, state FROM tombstones WHERE session_id = $id", ("$id", sessionId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return Task.FromResult(false);
        var tombGen = reader.GetInt64(0);
        var state = reader.GetString(1);
        // §11.6：Deleting 封版 → 全部拒绝（删除进行中，无任何提交可放行）；
        // Deleted → 同代次（≤ tombGen）拒绝（旧 incarnation 不复活），更新代次（> tombGen，重建）放行
        var blocked = state == "Deleting" || generation <= tombGen;
        return Task.FromResult(blocked);
    }

    private async Task EnsureSessionNotDeletedAsync(string sessionId, long generation)
    {
        if (await TombstoneBlocksAsync(sessionId, generation).ConfigureAwait(false))
            throw new RuntimePipelineException(ImErrorCodes.GenerationStale,
                $"会话 {sessionId} 已删除/封版（或 generation 失配），拒绝提交。");
    }

    private Task UpdateProjectionWatermarkAsync(IReadOnlyList<TurnEvent> events)
    {
        foreach (var group in events.GroupBy(e => e.TurnId))
        {
            var max = group.Max(e => e.EventIndex);
            Db.Execute("""
                INSERT INTO projections(turn_id, projected_index) VALUES($id, $max)
                ON CONFLICT(turn_id) DO UPDATE SET projected_index = MAX(projected_index, $max)
                """, ("$id", group.Key), ("$max", max));
        }
        return Task.CompletedTask;
    }

    private static TurnRow ReadTurn(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("turn_id")),
        r.GetString(r.GetOrdinal("session_id")),
        r.GetInt64(r.GetOrdinal("generation")),
        r.GetInt64(r.GetOrdinal("expected_revision")),
        r.GetString(r.GetOrdinal("origin")),
        r.GetString(r.GetOrdinal("status")),
        r.GetInt64(r.GetOrdinal("has_unsafe_attempt")) != 0,
        r.IsDBNull(r.GetOrdinal("unsafe_detail")) ? null : r.GetString(r.GetOrdinal("unsafe_detail")),
        r.IsDBNull(r.GetOrdinal("reply_target_json")) ? null : r.GetString(r.GetOrdinal("reply_target_json")));

    private static CommitRow ReadCommit(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("commit_id")),
        r.GetString(r.GetOrdinal("turn_id")),
        r.GetString(r.GetOrdinal("session_id")),
        r.GetInt64(r.GetOrdinal("generation")),
        r.GetInt64(r.GetOrdinal("revision")),
        r.GetString(r.GetOrdinal("state")),
        r.GetString(r.GetOrdinal("snapshot_dir")),
        r.IsDBNull(r.GetOrdinal("result_text")) ? null : r.GetString(r.GetOrdinal("result_text")),
        r.IsDBNull(r.GetOrdinal("outbox_plan_json")) ? null : r.GetString(r.GetOrdinal("outbox_plan_json")));
}

/// <summary>turns 行（内部）。</summary>
public sealed record TurnRow(
    string TurnId, string SessionId, long Generation, long ExpectedRevision,
    string Origin, string Status, bool HasUnsafeAttempt, string? UnsafeDetail, string? ReplyTargetJson);

/// <summary>commits 行（内部）。</summary>
public sealed record CommitRow(
    string CommitId, string TurnId, string SessionId, long Generation, long Revision,
    string State, string SnapshotDir, string? ResultText, string? OutboxPlanJson);
