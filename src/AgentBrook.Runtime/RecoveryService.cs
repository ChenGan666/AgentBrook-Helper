namespace AgentBrook.Runtime;

/// <summary>恢复动作记录（测试断言与人工核对依据）。</summary>
public sealed record RecoveryAction(string Action, string SessionId, string Detail);

/// <summary>恢复报告：动作序列 + 是否存在必须人工介入的会话/回合。</summary>
public sealed record RecoveryReport(IReadOnlyList<RecoveryAction> Actions, IReadOnlyList<string> RecoveryRequiredReasons)
{
    public bool RequiresHumanAttention => RecoveryRequiredReasons.Count > 0;
}

/// <summary>
/// 启动恢复对账（设计 §11.5 + §11.6 v4.2 前置校验）：
/// 先处理删除（Deleting 继续完成）/撤权/换号 → 取消被删会话的未决提交 → 再补合法提交（CURRENT 补切、
/// 投影修复、账务）→ 最后按状态矩阵处置 Running 回合（只读安全重试 / 非安全 RecoveryRequired）。
/// 恢复在获得会话级排他权后执行（调用方持租约或无并发写入时启动）。
/// </summary>
public sealed class RecoveryService(ImDatabase db, SessionFileSystem fs, CommitJournal journal, DeleteProtocol delete)
{
    public async Task<RecoveryReport> RecoverAsync(IReadOnlyCollection<string>? onlySessions = null)
    {
        var actions = new List<RecoveryAction>();
        var required = new List<string>();

        // ── 0. 未完成删除继续完成（删除优先于一切补提交）──
        var deleting = await ListSessionsInTombstoneStateAsync("Deleting").ConfigureAwait(false);
        foreach (var sessionId in deleting)
        {
            if (onlySessions is not null && !onlySessions.Contains(sessionId)) continue;
            await delete.ContinuePendingDeletionAsync(sessionId).ConfigureAwait(false);
            actions.Add(new RecoveryAction("delete-continued", sessionId, "Deleting → Deleted"));
        }

        // ── 1. Pending commits 对账（删除/撤权优先于补提交）──
        var pendingCommits = await journal.ListCommitsAsync("Pending").ConfigureAwait(false);
        foreach (var commit in pendingCommits)
        {
            if (onlySessions is not null && !onlySessions.Contains(commit.SessionId)) continue;

            // 前置校验①：tombstone / generation
            if (await journal.TombstoneBlocksAsync(commit.SessionId, commit.Generation).ConfigureAwait(false))
            {
                await journal.CancelPendingCommitsAsync(commit.SessionId, "tombstone/epoch precheck").ConfigureAwait(false);
                await journal.CancelTurnAsync(commit.TurnId, "session deleted before commit recovery").ConfigureAwait(false);
                actions.Add(new RecoveryAction("commit-cancelled", commit.SessionId,
                    $"{commit.CommitId} 被删除/封版前置校验取消（审计保留）"));
                continue;
            }

            var turn = await journal.GetTurnAsync(commit.TurnId).ConfigureAwait(false);
            var hasUnsafe = turn?.HasUnsafeAttempt ?? false;

            // 前置校验②：会话目录缺失且无 tombstone = 异常组合（外部删除），不静默重建
            if (!fs.SessionDirectoryExists(commit.SessionId) && fs.ReadCurrent(commit.SessionId) is null)
            {
                await journal.MarkRecoveryRequiredAsync(commit.TurnId, "session directory missing without tombstone").ConfigureAwait(false);
                required.Add($"{commit.SessionId}/{commit.TurnId}: 会话目录缺失且无 tombstone → RecoveryRequired");
                actions.Add(new RecoveryAction("recovery-required", commit.SessionId, commit.TurnId));
                continue;
            }

            var versionComplete = fs.IsVersionComplete(commit.SessionId, commit.Revision, commit.CommitId);

            if (!versionComplete)
            {
                // 步骤 2 写盘中断：目录作废删除；非安全 → RecoveryRequired，只读可安全重跑
                fs.DeleteVersion(commit.SessionId, commit.Revision);
                if (hasUnsafe)
                {
                    await journal.MarkRecoveryRequiredAsync(commit.TurnId, "incomplete version with unsafe attempt").ConfigureAwait(false);
                    required.Add($"{commit.SessionId}/{commit.TurnId}: 版本不完整且含非安全尝试 → RecoveryRequired");
                    actions.Add(new RecoveryAction("recovery-required", commit.SessionId, commit.TurnId));
                }
                else
                {
                    await journal.MarkFailedSafeAsync(commit.TurnId).ConfigureAwait(false);
                    actions.Add(new RecoveryAction("commit-discarded-safe", commit.SessionId,
                        $"{commit.CommitId} 版本不完整（只读回合），可安全重跑"));
                }
                continue;
            }

            var current = fs.ReadCurrent(commit.SessionId);
            if (current is null)
            {
                // 会话目录被外部删除但无 tombstone：异常组合
                await journal.MarkRecoveryRequiredAsync(commit.TurnId, "session directory missing without tombstone").ConfigureAwait(false);
                required.Add($"{commit.SessionId}/{commit.TurnId}: 会话目录缺失且无 tombstone → RecoveryRequired");
                continue;
            }

            if (current.Value.Revision < commit.Revision)
            {
                // F08：CommitPending 事务成功、切 CURRENT 前 → 补切同一完整快照
                await journal.FinishPendingCommitAsync(commit).ConfigureAwait(false);
                actions.Add(new RecoveryAction("commit-current-switched", commit.SessionId,
                    $"{commit.CommitId} 补切 CURRENT → r{commit.Revision}（不重跑模型）"));
            }
            else if (current.Value.Revision == commit.Revision)
            {
                // CURRENT 已在本提交：补投影/账务（F09/F12）
                await journal.FinalizeAccountingOnlyAsync(commit).ConfigureAwait(false);
                actions.Add(new RecoveryAction("commit-accounting-finished", commit.SessionId,
                    $"{commit.CommitId} 补投影+账务（不动 CURRENT）"));
            }
            else
            {
                // CURRENT 更晚：确认本 commit 已包含于已提交历史（仅版本号更大不构成依据）
                var historyCommit = await journal.GetCommitAsync(commit.CommitId).ConfigureAwait(false);
                var chainContains = fs.ReadMeta(commit.SessionId, current.Value.Revision) is { } laterMeta
                    && laterMeta.Revision >= commit.Revision;
                if (historyCommit is not null || chainContains)
                {
                    await journal.FinalizeAccountingOnlyAsync(commit).ConfigureAwait(false);
                    actions.Add(new RecoveryAction("commit-accounting-only", commit.SessionId,
                        $"{commit.CommitId} 已包含于已提交历史，只补账务"));
                }
                else
                {
                    await journal.MarkRecoveryRequiredAsync(commit.TurnId, "current ahead without commit history evidence").ConfigureAwait(false);
                    required.Add($"{commit.SessionId}/{commit.TurnId}: CURRENT 更晚但无法确认提交已入历史 → RecoveryRequired");
                }
            }
        }

        // ── 2. Running 回合处置（状态矩阵 §11.4）──
        var runningTurns = await journal.ListTurnsByStatusAsync("Running").ConfigureAwait(false);
        foreach (var turn in runningTurns)
        {
            if (onlySessions is not null && !onlySessions.Contains(turn.SessionId)) continue;
            if (await journal.TombstoneBlocksAsync(turn.SessionId, turn.Generation).ConfigureAwait(false))
            {
                await journal.CancelTurnAsync(turn.TurnId, "session deleted").ConfigureAwait(false);
                actions.Add(new RecoveryAction("turn-cancelled", turn.SessionId, turn.TurnId));
                continue;
            }
            if (turn.HasUnsafeAttempt)
            {
                // F04/F05：标记存在而结果未知 → 一律人工恢复，禁止自动执行
                await journal.MarkRecoveryRequiredAsync(turn.TurnId, "unsafe attempt with unknown result").ConfigureAwait(false);
                required.Add($"{turn.SessionId}/{turn.TurnId}: 非安全尝试结果未知 → RecoveryRequired");
            }
            else
            {
                // 全只读 → 安全重试类（S4 起 Inbox 重新调度）
                await journal.MarkFailedSafeAsync(turn.TurnId).ConfigureAwait(false);
                actions.Add(new RecoveryAction("turn-safe-retry", turn.SessionId, turn.TurnId));
            }
        }

        // ── 3. 孤儿版本目录清理（写盘中断且无 commits 行引用的半成品）──
        await CleanupOrphanVersionsAsync(onlySessions, actions).ConfigureAwait(false);

        return new RecoveryReport(actions, required);
    }

    private async Task CleanupOrphanVersionsAsync(IReadOnlyCollection<string>? onlySessions, List<RecoveryAction> actions)
    {
        var pendingBySession = (await journal.ListCommitsAsync("Pending").ConfigureAwait(false))
            .GroupBy(c => c.SessionId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Revision).ToHashSet());

        if (!Directory.Exists(fs.SessionsRoot)) return;

        foreach (var dir in Directory.GetDirectories(fs.SessionsRoot))
        {
            var sessionId = Path.GetFileName(dir);
            if (onlySessions is not null && !onlySessions.Contains(sessionId)) continue;

            foreach (var version in fs.ListVersions(sessionId))
            {
                var meta = fs.ReadMeta(sessionId, version);
                if (meta is not null) continue; // 完整目录交由 commits 对账处置，此处只清半成品
                if (pendingBySession.TryGetValue(sessionId, out var refs) && refs.Contains(version)) continue;
                fs.DeleteVersion(sessionId, version);
                actions.Add(new RecoveryAction("orphan-version-discarded", sessionId, $"versions/{version}（无 commits 引用的半成品）"));
            }
        }
    }

    private string DbRoot() => db.RootPath;

    private Task<List<string>> ListSessionsInTombstoneStateAsync(string state)
    {
        using var cmd = db.CreateCommand("SELECT session_id FROM tombstones WHERE state = $state", ("$state", state));
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read()) list.Add(reader.GetString(0));
        return Task.FromResult(list);
    }
}
