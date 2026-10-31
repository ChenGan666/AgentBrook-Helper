using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentBrook.Privacy;

/// <summary>词典快照条目（启用实体 × 启用别名的联查行；供 GlossaryDetector 建 AC 自动机）。</summary>
public sealed record GlossaryEntry(string Normalized, string Alias, long EntityId, string Category, string Canonical);

/// <summary>
/// 隐私词典 / 映射 SQLite 存储（15 号设计 §8）。
/// 访问模式对齐 A2AStore：单长连接 + SemaphoreSlim 门控 + WAL + foreign_keys + busy_timeout。
/// 热路径（中间件）只读 <see cref="Snapshot"/>；写操作仅词典管理 / 号段预留 / overlay 批量落库 / 设置修改。
/// </summary>
public sealed class PrivacyStore : IAsyncDisposable
{
    private const int TempNumberFloor = 900_000;   // 高位临时段下界（D16）；持久号段上界守护

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS entity (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            category        TEXT    NOT NULL,
            canonical       TEXT    NOT NULL,
            placeholder_no  INTEGER NOT NULL,
            source          TEXT    NOT NULL DEFAULT 'manual',
            enabled         INTEGER NOT NULL DEFAULT 1,
            note            TEXT,
            created_at      INTEGER NOT NULL,
            updated_at      INTEGER NOT NULL,
            last_used_at    INTEGER,
            use_count       INTEGER NOT NULL DEFAULT 0,
            UNIQUE (category, placeholder_no),
            UNIQUE (category, canonical)
        );
        CREATE INDEX IF NOT EXISTS ix_entity_cat ON entity(category, enabled);

        CREATE TABLE IF NOT EXISTS entity_alias (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            entity_id      INTEGER NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
            alias          TEXT    NOT NULL,
            normalized     TEXT    NOT NULL,
            match_mode     TEXT    NOT NULL DEFAULT 'auto',
            case_sensitive INTEGER NOT NULL DEFAULT 0,
            enabled        INTEGER NOT NULL DEFAULT 1
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ix_alias_norm ON entity_alias(normalized);
        CREATE INDEX IF NOT EXISTS ix_alias_entity ON entity_alias(entity_id);

        CREATE TABLE IF NOT EXISTS whitelist (
            id      INTEGER PRIMARY KEY AUTOINCREMENT,
            pattern TEXT NOT NULL,
            kind    TEXT NOT NULL DEFAULT 'literal',
            note    TEXT
        );

        CREATE TABLE IF NOT EXISTS settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    private const string DefaultCategories =
        """{"PHONE":true,"EMAIL":true,"IDCARD":true,"BANKCARD":true,"USCC":true,"LANDLINE":false,"PLATE":false,"IP":false,"URL":false,"PASSPORT":false,"SECRET_ASSIGN":false}""";

    private const string DefaultNerCategories =
        """{"PERSON":true,"ORG":true,"ADDRESS":true}""";

    private static readonly (string Key, string Value)[] DefaultSettings =
    [
        ("privacy.enabled", "0"),
        ("privacy.engine", "inprocess"),
        ("privacy.categories", DefaultCategories),
        ("privacy.egress_policy", "{}"),
        ("privacy.fail_open", "0"),
        ("privacy.mask_note", "1"),
        ("privacy.holdback_max", "24"),
        ("privacy.auto_register_limit", "200"),
        // P2 本地 NER（17 号方案 §8.4）
        ("privacy.ner.enabled", "0"),
        ("privacy.ner.model", "cluener_baseline"),   // P0a 裁决 A（2026-10-30）：准确档部署默认；轻量档 albert_tiny_zh / bert_tiny_zh 可选
        ("privacy.ner.threshold", "0.60"),   // P0a 实测：0.6 在 F1/覆盖召回上全面占优（17 号报告）
        ("privacy.ner.categories", DefaultNerCategories),
    ];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private bool _initialized;
    private long _version;
    private PrivacySnapshot _snapshot;

    public PrivacyStore(string databasePath)
    {
        DatabasePath = databasePath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
            Pooling = false,   // 单长连接模式本无池收益；且默认池会在 Dispose 后仍持有文件句柄——Windows 上锁死目录删除（跨平台实测 2026-10-31）
        };
        _connectionString = builder.ToString();
        _snapshot = EmptySnapshot(0);
    }

    public string DatabasePath { get; }

    /// <summary>不可变快照；任何写操作 bump Version 并触发 <see cref="Changed"/>。</summary>
    public PrivacySnapshot Snapshot => _snapshot;

    /// <summary>快照版本变化（词典 / 设置写操作后触发）。</summary>
    public event Action? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();
            Execute("""
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
                """);
            Execute(SchemaSql);
            Execute("INSERT INTO schema_version(version) SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM schema_version);");
            foreach (var (key, value) in DefaultSettings)
            {
                Execute("INSERT OR IGNORE INTO settings(key, value) VALUES($k, $v)",
                    ("$k", key), ("$v", value));
            }
            await ReloadSnapshotAsync();
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── 设置 ─────────────────────────────────────────────────────────────

    public async Task<T> GetSettingAsync<T>(string key, T fallback)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureInit();
            var raw = ReadSetting(null, key);
            return ConvertSetting(raw, fallback);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetSettingAsync(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await _gate.WaitAsync();
        try
        {
            EnsureInit();
            WriteSetting(null, key, value);
            await ReloadSnapshotAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── 号段（D16）：持久号段预分配；上界 ≥ 900000 抛异常（与临时段相撞守护） ──

    public async Task<int> ReserveSeatAsync(string category, int count = 64)
    {
        category = ValidateCategory(category);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        await _gate.WaitAsync();
        try
        {
            EnsureInit();
            using var tx = _connection!.BeginTransaction();
            var upper = ReserveSeatCore(tx, category, count);
            tx.Commit();
            await ReloadSnapshotAsync();
            return upper;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── 词典管理（设置页 / CLI / MCP 工具） ──────────────────────────────

    public async Task<EntityInfo> AddEntityAsync(string category, string canonical,
        IEnumerable<string>? aliases = null, string? note = null)
    {
        category = ValidateCategory(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonical);
        canonical = canonical.Trim();
        await _gate.WaitAsync();
        try
        {
            EnsureInit();
            using var tx = _connection!.BeginTransaction();
            var entity = InsertEntityCore(tx, category, canonical, "manual", note, aliases);
            tx.Commit();
            await ReloadSnapshotAsync();
            return entity;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<EntityInfo>> ListEntitiesAsync(string? category = null,
        string? search = null, bool includeDisabled = true)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureInit();
            var sql = new StringBuilder("""
                SELECT e.id, e.category, e.canonical, e.placeholder_no, e.source, e.enabled,
                       e.note, e.created_at, e.updated_at, e.last_used_at, e.use_count
                FROM entity e
                WHERE 1=1
                """);
            var args = new List<(string, object?)>();
            if (!string.IsNullOrEmpty(category))
            {
                sql.Append(" AND e.category = $cat");
                args.Add(("$cat", category));
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                sql.Append("""
                     AND (e.canonical LIKE $pat ESCAPE '\'
                          OR EXISTS (SELECT 1 FROM entity_alias a
                                     WHERE a.entity_id = e.id AND a.normalized LIKE $pat ESCAPE '\'))
                    """);
                args.Add(("$pat", "%" + EscapeLike(search.Trim().ToLowerInvariant()) + "%"));
            }
            if (!includeDisabled)
            {
                sql.Append(" AND e.enabled = 1");
            }
            sql.Append(" ORDER BY e.category, e.placeholder_no");
            var rows = new List<EntityInfo>();
            using (var cmd = _connection!.CreateCommand())
            {
                cmd.CommandText = sql.ToString();
                foreach (var (name, value) in args)
                {
                    cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                }
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(MapEntity(reader));
                }
            }
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AliasInfo>> ListAliasesAsync(long entityId)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureInit();
            var rows = new List<AliasInfo>();
            using (var cmd = _connection!.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT id, entity_id, alias, normalized, match_mode, case_sensitive, enabled
                    FROM entity_alias WHERE entity_id = $eid ORDER BY id
                    """;
                cmd.Parameters.AddWithValue("$eid", entityId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new AliasInfo(
                        reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                        reader.GetString(3), reader.GetString(4),
                        reader.GetInt64(5) != 0, reader.GetInt64(6) != 0));
                }
            }
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>一次性联查全部启用别名（词典快照数据源；快照重建时调用，非热路径逐条查询）。</summary>
    public async Task<IReadOnlyList<GlossaryEntry>> LoadGlossaryAsync()
    {
        await _gate.WaitAsync();
        try
        {
            EnsureInit();
            var rows = new List<GlossaryEntry>();
            using (var cmd = _connection!.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT a.normalized, a.alias, a.entity_id, e.category, e.canonical
                    FROM entity_alias a JOIN entity e ON e.id = a.entity_id
                    WHERE a.enabled = 1 AND e.enabled = 1
                    ORDER BY a.normalized
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new GlossaryEntry(
                        reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                        reader.GetString(3), reader.GetString(4)));
                }
            }
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetEntityEnabledAsync(long entityId, bool enabled)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureInit();
            Execute("UPDATE entity SET enabled = $e, updated_at = $now WHERE id = $id",
                ("$e", enabled ? 1L : 0L), ("$now", NowUnix()), ("$id", entityId));
            await ReloadSnapshotAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>删除实体（FK 级联删除别名）。历史存原文，删除只影响之后回合。</summary>
    public async Task DeleteEntityAsync(long entityId)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureInit();
            Execute("DELETE FROM entity WHERE id = $id", ("$id", entityId));
            await ReloadSnapshotAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAliasAsync(long entityId, string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        await _gate.WaitAsync();
        try
        {
            EnsureInit();
            using var tx = _connection!.BeginTransaction();
            InsertAliasCore(tx, entityId, alias.Trim());
            tx.Commit();
            await ReloadSnapshotAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── overlay 批量落库（响应完成后由引擎触发；幂等） ───────────────────

    public async Task FlushOverlayAsync(IReadOnlyList<PendingEntity> entities,
        IReadOnlyList<UsageDelta> usage)
    {
        if (entities.Count == 0 && usage.Count == 0)
        {
            return;
        }
        await _gate.WaitAsync();
        try
        {
            EnsureInit();
            using var tx = _connection!.BeginTransaction();
            var now = NowUnix();
            foreach (var pending in entities)
            {
                ValidateCategory(pending.Category);
                // 归并语义：同 canonical 已存在（他进程先落库）→ 丢弃本方编号，保留先到行
                Execute(tx, """
                    INSERT INTO entity(category, canonical, placeholder_no, source, enabled, created_at, updated_at)
                    SELECT $cat, $can, $no, $src, 1, $now, $now
                    WHERE NOT EXISTS (SELECT 1 FROM entity WHERE category = $cat AND canonical = $can)
                    """,
                    ("$cat", pending.Category), ("$can", pending.Canonical), ("$no", (long)pending.PlaceholderNo),
                    ("$src", pending.Source), ("$now", now));
                // canonical 补一行别名（与 AddEntityCore 不变式一致；normalized 唯一去重，冲突静默忽略）
                var norm = pending.Canonical.ToLowerInvariant();
                Execute(tx, """
                    INSERT INTO entity_alias(entity_id, alias, normalized, match_mode, case_sensitive, enabled)
                    SELECT e.id, e.canonical, $norm, 'auto', 0, 1
                    FROM entity e
                    WHERE e.category = $cat AND e.canonical = $can
                      AND NOT EXISTS (SELECT 1 FROM entity_alias a WHERE a.normalized = $norm)
                    """,
                    ("$cat", pending.Category), ("$can", pending.Canonical), ("$norm", norm));
            }
            foreach (var delta in usage)
            {
                Execute(tx, """
                    UPDATE entity
                    SET use_count = use_count + $delta,
                        last_used_at = MAX(COALESCE(last_used_at, 0), $last),
                        updated_at = $now
                    WHERE category = $cat AND canonical = $can
                    """,
                    ("$delta", (long)delta.UseDelta), ("$last", delta.LastUsedAtUnix),
                    ("$now", now), ("$cat", delta.Category), ("$can", delta.Canonical));
            }
            tx.Commit();
            await ReloadSnapshotAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── 白名单 ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WhitelistEntry>> ListWhitelistAsync()
    {
        await _gate.WaitAsync();
        try
        {
            EnsureInit();
            var rows = new List<WhitelistEntry>();
            using (var cmd = _connection!.CreateCommand())
            {
                cmd.CommandText = "SELECT id, pattern, kind, note FROM whitelist ORDER BY id";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new WhitelistEntry(reader.GetInt64(0), reader.GetString(1),
                        reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
                }
            }
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WhitelistEntry> AddWhitelistAsync(string pattern, string kind = "literal", string? note = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        if (kind is not ("literal" or "regex"))
        {
            throw new ArgumentException($"未知白名单类型：{kind}", nameof(kind));
        }
        // literal 存规范化形（ASCII 小写 + 空白折叠；CJK 原样）；regex 原样
        var stored = kind == "literal" ? NormalizeLiteral(pattern) : pattern;
        await _gate.WaitAsync();
        try
        {
            EnsureInit();
            long id;
            using (var cmd = _connection!.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO whitelist(pattern, kind, note) VALUES($p, $k, $n); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$p", stored);
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$n", (object?)note ?? DBNull.Value);
                id = (long)cmd.ExecuteScalar()!;
            }
            await ReloadSnapshotAsync();
            return new WhitelistEntry(id, stored, kind, note);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveWhitelistAsync(long id)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureInit();
            Execute("DELETE FROM whitelist WHERE id = $id", ("$id", id));
            await ReloadSnapshotAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── CSV 导入导出（附录 B：category,term,aliases,note[,enabled]） ─────

    /// <summary>批量导入：非法类别 fail-fast（带行号）；重复 normalized 跳过并计数。</summary>
    public async Task<CsvImportResult> ImportCsvAsync(TextReader csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var records = PrivacyCsv.Parse(csv);
        if (records.Count == 0)
        {
            return new CsvImportResult(0, 0);
        }
        var start = IndexOfHeader(records) + 1;   // 无表头时 IndexOfHeader = -1 → 从首行开始
        await _gate.WaitAsync();
        try
        {
            EnsureInit();
            var imported = 0;
            var skipped = 0;
            using var tx = _connection!.BeginTransaction();
            for (var i = start; i < records.Count; i++)
            {
                var line = i + 1;   // 含表头的物理行号（近似；多行字段时为记录起始行）
                var row = records[i];
                if (row.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }
                if (row.Count < 2)
                {
                    throw new PrivacyFormatException($"第 {line} 行：至少需要 category 与 term 两列");
                }
                string category;
                try
                {
                    category = ValidateCategory(row[0]);
                }
                catch (ArgumentException ex)
                {
                    throw new PrivacyFormatException($"第 {line} 行：无效类别 '{row[0].Trim()}'（{ex.Message}）");
                }
                var term = row[1].Trim();
                if (term.Length == 0)
                {
                    throw new PrivacyFormatException($"第 {line} 行：term 为空");
                }
                var aliases = row.Count > 2 && row[2].Length > 0
                    ? row[2].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : [];
                var note = row.Count > 3 && row[3].Length > 0 ? row[3] : null;
                try
                {
                    InsertEntityCore(tx, category, term, "import", note, aliases);
                    imported++;
                }
                catch (PrivacyConflictException)
                {
                    skipped++;   // normalized / canonical 冲突：跳过并计数（批量场景策略）
                }
            }
            tx.Commit();
            await ReloadSnapshotAsync();
            return new CsvImportResult(imported, skipped);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>导出全部实体（含停用项，多一列 enabled）。</summary>
    public async Task ExportCsvAsync(TextWriter csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var entities = await ListEntitiesAsync(includeDisabled: true);
        var aliasMap = new Dictionary<long, List<string>>();
        foreach (var entity in entities)
        {
            var aliases = await ListAliasesAsync(entity.Id);
            // canonical 本身也是一行别名，导出时跳过它（term 列已是 canonical）
            var extra = aliases
                .Where(a => !string.Equals(a.Normalized, entity.Canonical.ToLowerInvariant(), StringComparison.Ordinal))
                .Select(a => a.Alias);
            aliasMap[entity.Id] = [.. extra];
        }
        csv.WriteLine("category,term,aliases,note,enabled");
        foreach (var entity in entities)
        {
            csv.WriteLine(string.Join(',',
                PrivacyCsv.Escape(entity.Category),
                PrivacyCsv.Escape(entity.Canonical),
                PrivacyCsv.Escape(string.Join(';', aliasMap[entity.Id])),
                PrivacyCsv.Escape(entity.Note ?? ""),
                entity.Enabled ? "1" : "0"));
        }
    }

    // ── 内部核心 ─────────────────────────────────────────────────────────

    /// <summary>表头行下标（首列 = category）；无表头返回 -1。</summary>
    private static int IndexOfHeader(IReadOnlyList<IReadOnlyList<string>> records)
    {
        for (var i = 0; i < records.Count; i++)
        {
            if (records[i].Count > 0 &&
                string.Equals(records[i][0].Trim(), "category", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private EntityInfo InsertEntityCore(SqliteTransaction tx, string category, string canonical,
        string source, string? note, IEnumerable<string>? aliases)
    {
        var now = NowUnix();
        var canonicalNormalized = canonical.ToLowerInvariant();
        // 先查后插（事务内全预检）：别名冲突不能留下"无别名实体"的半行——导入跳过语义依赖原子性
        if (QueryScalar(tx, "SELECT id FROM entity WHERE category = $c AND canonical = $n",
                ("$c", category), ("$n", canonical)) is not null)
        {
            throw new PrivacyConflictException($"实体已存在：[{category}] {canonical}");
        }
        if (QueryScalar(tx, "SELECT id FROM entity_alias WHERE normalized = $n",
                ("$n", canonicalNormalized)) is not null)
        {
            throw new PrivacyConflictException($"别名已存在：{canonical}");
        }
        if (aliases is not null)
        {
            foreach (var alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    continue;
                }
                if (QueryScalar(tx, "SELECT id FROM entity_alias WHERE normalized = $n",
                        ("$n", alias.Trim().ToLowerInvariant())) is not null)
                {
                    throw new PrivacyConflictException($"别名已存在：{alias.Trim()}");
                }
            }
        }
        var no = ReserveSeatCore(tx, category, 1);
        long id;
        using (var cmd = _connection!.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO entity(category, canonical, placeholder_no, source, enabled, note, created_at, updated_at)
                VALUES($cat, $can, $no, $src, 1, $note, $now, $now); SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$cat", category);
            cmd.Parameters.AddWithValue("$can", canonical);
            cmd.Parameters.AddWithValue("$no", (long)no);
            cmd.Parameters.AddWithValue("$src", source);
            cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            id = (long)cmd.ExecuteScalar()!;
        }
        // canonical 本身也存一行别名（匹配入口与写法入口统一走 alias 表）
        InsertAliasCore(tx, id, canonical);
        if (aliases is not null)
        {
            foreach (var alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    continue;
                }
                InsertAliasCore(tx, id, alias.Trim());
            }
        }
        return new EntityInfo(id, category, canonical, no, source, true, note, now, now, null, 0);
    }

    private void InsertAliasCore(SqliteTransaction tx, long entityId, string alias)
    {
        var normalized = alias.ToLowerInvariant();
        if (QueryScalar(tx, "SELECT id FROM entity_alias WHERE normalized = $n", ("$n", normalized)) is not null)
        {
            throw new PrivacyConflictException($"别名已存在：{alias}");
        }
        Execute(tx, """
            INSERT INTO entity_alias(entity_id, alias, normalized, match_mode, case_sensitive, enabled)
            VALUES($eid, $a, $n, 'auto', 0, 1)
            """,
            ("$eid", entityId), ("$a", alias), ("$n", normalized));
    }

    private int ReserveSeatCore(SqliteTransaction tx, string category, int count)
    {
        var key = $"privacy.seat.{category}";
        var current = ConvertSetting(ReadSetting(tx, key), 0);
        var upper = current + count;
        if (upper >= TempNumberFloor)
        {
            throw new InvalidOperationException(
                $"类别 {category} 号段上界 {upper} ≥ {TempNumberFloor}（与临时段相撞守护，D16）");
        }
        WriteSetting(tx, key, upper.ToString(CultureInfo.InvariantCulture));
        return upper;
    }

    private async Task ReloadSnapshotAsync()
    {
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = _connection!.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value FROM settings";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                raw[reader.GetString(0)] = reader.GetString(1);
            }
        }
        _snapshot = BuildSnapshot(Interlocked.Increment(ref _version), raw);
        Changed?.Invoke();
        await Task.CompletedTask;
    }

    private PrivacySnapshot BuildSnapshot(long version, IReadOnlyDictionary<string, string> raw)
    {
        var categories = ParseJsonDict<bool>(raw, "privacy.categories");
        var egress = ParseJsonDict<string>(raw, "privacy.egress_policy");
        var nerCategories = ParseJsonDict<bool>(raw, "privacy.ner.categories");
        return new PrivacySnapshot(
            Version: version,
            MasterEnabled: ConvertSetting(raw.GetValueOrDefault("privacy.enabled"), false),
            Engine: raw.GetValueOrDefault("privacy.engine") ?? "inprocess",
            FailOpen: ConvertSetting(raw.GetValueOrDefault("privacy.fail_open"), false),
            MaskNote: ConvertSetting(raw.GetValueOrDefault("privacy.mask_note"), true),
            HoldbackMax: ConvertSetting(raw.GetValueOrDefault("privacy.holdback_max"), 24),
            AutoRegisterLimit: ConvertSetting(raw.GetValueOrDefault("privacy.auto_register_limit"), 200),
            Categories: categories,
            EgressPolicy: egress,
            NerEnabled: ConvertSetting(raw.GetValueOrDefault("privacy.ner.enabled"), false),
            NerModel: raw.GetValueOrDefault("privacy.ner.model") ?? "cluener_baseline",
            NerThreshold: ConvertSetting(raw.GetValueOrDefault("privacy.ner.threshold"), 0.60),
            NerCategories: nerCategories,
            RawSettings: raw);
    }

    private static Dictionary<string, T> ParseJsonDict<T>(IReadOnlyDictionary<string, string> raw, string key)
    {
        var value = raw.GetValueOrDefault(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Dictionary<string, T>();
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, T>>(value, JsonOpts)
                   ?? new Dictionary<string, T>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, T>();
        }
    }

    private static T ConvertSetting<T>(string? raw, T fallback)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }
        if (typeof(T) == typeof(string))
        {
            return (T)(object)raw;
        }
        if (typeof(T) == typeof(bool))
        {
            // 设置值沿用 0/1 记法（§8.4），同时兼容 true/false
            return (T)(object)(raw is "1" or "true" or "True" or "TRUE");
        }
        if (typeof(T) == typeof(int))
        {
            return (T)(object)int.Parse(raw, CultureInfo.InvariantCulture);
        }
        if (typeof(T) == typeof(long))
        {
            return (T)(object)long.Parse(raw, CultureInfo.InvariantCulture);
        }
        if (typeof(T) == typeof(double))
        {
            return (T)(object)double.Parse(raw, CultureInfo.InvariantCulture);
        }
        return JsonSerializer.Deserialize<T>(raw, JsonOpts) ?? fallback;
    }

    private string? ReadSetting(SqliteTransaction? tx, string key)
    {
        using var cmd = _connection!.CreateCommand();
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        var result = cmd.ExecuteScalar();
        return result as string;
    }

    private void WriteSetting(SqliteTransaction? tx, string key, string value)
    {
        Execute(tx, "INSERT INTO settings(key, value) VALUES($k, $v) "
            + "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$k", key), ("$v", value));
    }

    private void Execute(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        cmd.ExecuteNonQuery();
    }

    private void Execute(SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _connection!.CreateCommand();
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        cmd.ExecuteNonQuery();
    }

    private object? QueryScalar(SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return cmd.ExecuteScalar();
    }

    private static EntityInfo MapEntity(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), (int)r.GetInt64(3),
        r.GetString(4), r.GetInt64(5) != 0,
        r.IsDBNull(6) ? null : r.GetString(6),
        r.GetInt64(7), r.GetInt64(8),
        r.IsDBNull(9) ? null : r.GetInt64(9),
        r.GetInt64(10));

    private static string EscapeLike(string input) =>
        input.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>白名单 literal 规范化（ASCII 小写 + 空白折叠；CJK 原样）。</summary>
    private static string NormalizeLiteral(string input) =>
        System.Text.RegularExpressions.Regex.Replace(input.Trim(), "\\s+", " ").ToLowerInvariant();

    private static string ValidateCategory(string category)
    {
        // 开放类别（用户/模型可加 KEY、SECRET 等任意类）：归一化后代入，不再限内置白名单
        var norm = PrivacyCategories.Normalize(category)
            ?? throw new ArgumentException($"类别无效：{category}", nameof(category));
        return norm;
    }

    private void EnsureInit()
    {
        if (!_initialized || _connection is null)
        {
            throw new InvalidOperationException("PrivacyStore 未初始化：请先调用 InitializeAsync。");
        }
    }

    private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static PrivacySnapshot EmptySnapshot(long version) => new(
        version, false, "inprocess", false, true, 24, 200,
        new Dictionary<string, bool>(), new Dictionary<string, string>(),
        false, "cluener_baseline", 0.60, new Dictionary<string, bool>(), new Dictionary<string, string>());

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }
            _connection?.Dispose();
            _connection = null;
            _initialized = false;
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private bool _disposed;
}
