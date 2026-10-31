namespace AgentBrook.Privacy;

/// <summary>占位符分配结果。IsTemp = 高位临时段 900001–999999（不落库）；IsNew = 本进程首次登记。</summary>
public sealed record Allocation(int Number, bool IsTemp, bool IsNew, string Tag, string Category, string Canonical);

/// <summary>
/// 占位符注册表（15 号设计 §6.1 / §8.1，词典域状态——由引擎持有，引擎无会话集合状态）：
/// 持久号段池（AddOwnedRange 注入引擎保留的区间）+ 高位临时段（超 auto 上限或池空时使用，
/// 进程内稳定不随回合重置——同一实体标签不漂移）+ 实体映射（含停用实体的抑制判断）。
/// </summary>
public sealed class PlaceholderRegistry
{
    private const int TempFloor = 900_001;
    private const int TempCeiling = 999_999;

    private readonly int _autoRegisterLimit;
    private readonly Dictionary<string, EntityInfo> _persisted = new();   // key → 实体（含停用，供抑制判断）
    private readonly Dictionary<string, int> _nextNo = new();             // category → 下一个可分配持久号
    private readonly Dictionary<string, int> _high = new();               // category → 拥有区间上界
    private readonly Dictionary<string, int> _allocated = new();          // key → 新分配持久号（待落库）
    private readonly Dictionary<string, int> _temp = new();               // key → 临时号
    private readonly Dictionary<string, UsageDelta> _usage = new();
    private readonly List<PendingEntity> _pending = [];
    private int _tempNext = TempFloor;
    private int _autoCount;

    public PlaceholderRegistry(IEnumerable<EntityInfo> persistedEntities, int autoRegisterLimit = 200)
    {
        _autoRegisterLimit = autoRegisterLimit;
        foreach (var entity in persistedEntities)
        {
            _persisted[Key(entity.Category, entity.Canonical)] = entity;
        }
    }

    /// <summary>注入引擎保留到的号段区间 [low, high]（启动 / 池耗尽再预留时调用）。</summary>
    public void AddOwnedRange(string category, int low, int high)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(low, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(high, TempFloor - 1);
        if (_nextNo.TryGetValue(category, out var current) && current <= high)
        {
            return;   // 已有可用区间且未耗尽
        }
        _nextNo[category] = low;
        _high[category] = high;
    }

    /// <summary>
    /// 取（或登记）实体的占位符。命中停用实体返回 null（Masker 跳过该区段 = 不再脱敏）。
    /// 新登记受每回合 auto 上限与号段池约束，超限走高位临时段。
    /// <paramref name="autoFill"/>=false（P2 NER 命中）：新登记**直接走高位临时段**——
    /// 不进 overlay、不耗号段、不进 DrainPending、不占 auto 计数（17 号 §3.3 防误报固化）；
    /// 词典 / 已登记实体的复用不受影响（同实体同标签）。
    /// </summary>
    public Allocation? GetOrRegister(string category, string canonical, bool autoFill = true)
    {
        var key = Key(category, canonical);
        if (_persisted.TryGetValue(key, out var entity))
        {
            if (!entity.Enabled)
            {
                return null;   // 停用实体：不再脱敏
            }
            AddUsage(category, canonical);
            return new Allocation(entity.PlaceholderNo, false, false,
                $"[{category}_{entity.PlaceholderNo}]", category, canonical);
        }
        if (_allocated.TryGetValue(key, out var no))
        {
            AddUsage(category, canonical);
            return new Allocation(no, false, false, $"[{category}_{no}]", category, canonical);
        }
        if (_temp.TryGetValue(key, out var tempNo))
        {
            AddUsage(category, canonical);
            return new Allocation(tempNo, true, false, $"[{category}_{tempNo}]", category, canonical);
        }

        // 新登记
        if (autoFill && _autoCount < _autoRegisterLimit && HasPool(category))
        {
            no = _nextNo.GetValueOrDefault(category, 1);
            _nextNo[category] = no + 1;
            _allocated[key] = no;
            _pending.Add(new PendingEntity(category, canonical, no, "auto"));
            _autoCount++;
            AddUsage(category, canonical);
            return new Allocation(no, false, true, $"[{category}_{no}]", category, canonical);
        }
        if (_tempNext > TempCeiling)
        {
            throw new InvalidOperationException($"高位临时段耗尽（{TempFloor}–{TempCeiling}）。");
        }
        tempNo = _tempNext++;
        _temp[key] = tempNo;
        if (autoFill)
        {
            _autoCount++;
            AddUsage(category, canonical);
        }
        return new Allocation(tempNo, true, true, $"[{category}_{tempNo}]", category, canonical);
    }

    /// <summary>回合边界（引擎在每次响应开始时调用）：auto 登记计数清零；临时映射不受影响。</summary>
    public void BeginTurn() => _autoCount = 0;

    /// <summary>
    /// 检测结果自动入词典（去重）：待写实体 + 用量 → Store 批量落库。
    /// 去重三层：① 注册表 canonical 归一（138-1234-5678 ≡ 13812345678，同值只登记一次）；
    /// ② entity UNIQUE(category, canonical) + INSERT OR IGNORE（跨进程 / 跨重启）；
    /// ③ 别名 normalized 唯一（canonical 别名行不重复）。
    /// 引擎接入后由 IPrivacyEngine.FlushAsync 在响应完成后调用同一链路；本方法是接入前的便捷入口。
    /// </summary>
    public async Task FlushToStoreAsync(PrivacyStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var (entities, usage) = DrainPending();
        await store.FlushOverlayAsync(entities, usage);
    }

    /// <summary>取走待落库实体与累计用量（引擎 FlushAsync 时调用；幂等）。</summary>
    public (IReadOnlyList<PendingEntity> Entities, IReadOnlyList<UsageDelta> Usage) DrainPending()
    {
        var entities = _pending.ToArray();
        _pending.Clear();
        var usage = _usage.Values.ToArray();
        _usage.Clear();
        return (entities, usage);
    }

    /// <summary>
    /// 词典变更采纳（RefreshAsync 调用）：整表替换持久映射（含停用 → 抑制判断更新）；
    /// 同 canonical 的内存分配（新分配号 / 临时号）让位于词典编号——此后命中返回词典值。
    /// 已删除词条移出映射（下次命中重新自动登记）。**不影响其他实体的临时映射**（v5 稳定性）。
    /// </summary>
    public void UpsertPersisted(IEnumerable<EntityInfo> entities)
    {
        var valid = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            var key = Key(entity.Category, entity.Canonical);
            valid.Add(key);
            _persisted[key] = entity;
            _allocated.Remove(key);
            _temp.Remove(key);
        }
        foreach (var stale in _persisted.Keys.Where(k => !valid.Contains(k)).ToList())
        {
            _persisted.Remove(stale);
            _allocated.Remove(stale);
            _temp.Remove(stale);
        }
    }

    private bool HasPool(string category) =>
        _nextNo.TryGetValue(category, out var next) && _high.TryGetValue(category, out var high) && next <= high;

    private void AddUsage(string category, string canonical)
    {
        var key = Key(category, canonical);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_usage.TryGetValue(key, out var existing))
        {
            _usage[key] = existing with
            {
                UseDelta = existing.UseDelta + 1,
                LastUsedAtUnix = Math.Max(existing.LastUsedAtUnix, now),
            };
        }
        else
        {
            _usage[key] = new UsageDelta(category, canonical, 1, now);
        }
    }

    private static string Key(string category, string canonical) => $"{category}\u0001{canonical}";
}
