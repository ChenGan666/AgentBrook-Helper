using System.Text;
using System.Collections.Concurrent;
using System.Text.Json;
using AgentBrook.Plugin.Abstractions;
using AgentBrook.Privacy;
using AgentBrook.Privacy.Ner;
using Microsoft.Extensions.AI;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AgentBrook.Plugin.PrivacyMasking.Tests")]

namespace AgentBrook.Plugin.PrivacyMasking;

/// <summary>
/// 隐私脱敏插件入口（doc/18 首个标准插件）：实现 IHelperPlugin 生命周期，
/// 以 IChatClientDecorator 能力挂入模型管道（L4 壳），以 ISettingsContributor
/// 贡献设置页（总开关/阈值/识别模型安装与自检）。设置真源 = 宿主 state.json，
/// Initialize 时同步到 PrivacyStore（引擎快照热路径）。
/// </summary>
[PluginEntry("privacy-masking")]
public sealed class PrivacyMaskingPlugin : IHelperPlugin, IChatClientDecorator, ISettingsContributor, IPluginTurnTelemetry, IDisplayTextRestorer
{
    private const string DefaultModel = "cluener_baseline";   // P0a 裁决 A（2026-10-30）：准确档部署默认

    private IPluginHostContext? _host;
    private PrivacyStore? _store;
    private PrivacyEngine? _engine;
    private PluginModelManager? _models;
    private int _refreshQueued;
    private static readonly JsonSerializerOptions ActionJsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    // 插件级共享会话映射（v1.0.11）：同进程所有壳实例（主客户端/子 Agent/管线重建）共用——
    // 否则标签分散在多壳各自的映射里，还原时"部分标签没有替换"（真机案例）
    private readonly ConcurrentDictionary<string, string> _sessionEmitted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionEscapes = new(StringComparer.Ordinal);
    private int _settingsSyncQueued;
    private PluginTurnStats? _lastTurnStats;
    private string SessionMapsPath => Path.Combine(_host?.WorkspaceRoot ?? ".", "privacy", "session-maps.json");
    private volatile int _mapsSaving;
    private bool _initialized;

    public string Id => "privacy-masking";
    public string DisplayName => "隐私脱敏";
    public string Version => "1.0.12";

    public async Task InitializeAsync(IPluginHostContext host, CancellationToken cancellationToken = default)
    {
        _host = host;
        Directory.CreateDirectory(Path.Combine(host.WorkspaceRoot, "privacy"));

        _store = new PrivacyStore(Path.Combine(host.WorkspaceRoot, "privacy", "privacy.db"));
        await _store.InitializeAsync(cancellationToken);

        LoadSessionMaps();
        await SyncSettingsFromHostAsync();

        _engine = await PrivacyEngine.CreateAsync(_store);

        // 模型按需下载管理（服务端插件目录；安装后自动四步自检）
        // 下载走宿主统一服务（IPluginHostContext.Download，升级功能同源：超时/进度集中维护）
        var modelsDir = Path.Combine(host.WorkspaceRoot, "privacy", "models");
        _models = new PluginModelManager(modelsDir, host.Download, m => host.Log(m));

        // 快照热更新：设置/词典变更 → 重建识别层（防重入：Changed 由 Refresh 内部写操作触发）
        _engine.Changed += OnEngineChanged;

        _initialized = true;
        host.Log("隐私脱敏插件已初始化（默认关闭，可在设置中开启）");
    }

    public async Task ShutdownAsync()
    {
        if (_engine is not null)
        {
            _engine.Changed -= OnEngineChanged;
            await _engine.DisposeAsync();
            _engine = null;
        }
        _initialized = false;
    }

    // ── IChatClientDecorator ─────────────────────────────────────────────

    private SensitiveMaskingChatClient? _activeShell;

    public IChatClient? Decorate(IChatClient inner)
    {
        var engine = _engine ?? throw new InvalidOperationException("插件未初始化");
        if (!engine.Snapshot.MasterEnabled)
        {
            return null;   // 总开关关闭 → 不装饰（零开销直通）
        }
        var shell = new SensitiveMaskingChatClient(inner, engine, _host?.Language ?? "zh", OnTurnEnd,
            _sessionEmitted, _sessionEscapes, PersistSessionMaps);
        _activeShell = shell;   // 落盘回调取快照用（管线内单实例，PipelineHost 按版本缓存）
        return shell;
    }

    // ── 会话映射持久化（跨进程还原：文件里的字面占位符重启后仍可还原） ──


    private void LoadSessionMaps()
    {
        try
        {
            var path = SessionMapsPath;
            if (!File.Exists(path))
            {
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("emitted", out var em) && em.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in em.EnumerateObject())
                {
                    _sessionEmitted[p.Name] = p.Value.GetString() ?? "";
                }
            }
            if (root.TryGetProperty("escapes", out var es) && es.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in es.EnumerateObject())
                {
                    _sessionEscapes[p.Name] = p.Value.GetString() ?? "";
                }
            }
            _host?.Log($"已加载会话映射（emitted {_sessionEmitted.Count} 条）");
        }
        catch (Exception ex)
        {
            _host?.Log($"会话映射读取失败（按空映射继续）：{ex.Message}");
        }
    }

    /// <summary>回合结束时由壳回调：快照当前映射原子落盘（含真实值，与 privacy.db 同级同敏感级）。</summary>
    private void PersistSessionMaps()
    {
        if (Interlocked.Exchange(ref _mapsSaving, 1) == 1)
        {
            return;   // 已有保存进行中（本回合的增量下回合落盘，可接受）
        }
        _ = Task.Run(() =>
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    emitted = _sessionEmitted.ToArray().ToDictionary(kv => kv.Key, kv => kv.Value),
                    escapes = _sessionEscapes.ToArray().ToDictionary(kv => kv.Key, kv => kv.Value),
                });
                var path = SessionMapsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, payload);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                _host?.Log($"会话映射落盘失败：{ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _mapsSaving, 0);
            }
        });
    }

    // ── IPluginTurnTelemetry（T5.3 徽标/悬停数据源） ────────────────────

    private void OnTurnEnd(int masked, string? maskedUserText, bool images)
    {
        _lastTurnStats = new PluginTurnStats(masked, maskedUserText, images,
            _engine?.Snapshot.MaskNote ?? false);
        _host?.Log($"[diag] turn end: masked={masked}, shellEmitted={_activeShell?.EmittedCount ?? -1}, preview={maskedUserText?[..Math.Min(40, maskedUserText.Length)]}");
    }

    /// <summary>最近回合的脱敏统计；宿主在 TurnCompleted 后查询（徽标 + "模型实际看到的"悬停）。</summary>
    public PluginTurnStats? GetLastTurnStats() => _lastTurnStats;

    // ── 测试钩子（internal：单元测试直接驱动回合回调/壳注入） ───────────

    internal void OnTurnEndForTest(int masked) => OnTurnEnd(masked, null, false);

    internal void AttachShellForTest(AgentBrook.Plugin.PrivacyMasking.SensitiveMaskingChatClient shell) =>
        _activeShell = shell;

    /// <summary>测试访问器：共享会话映射（还原链路断言用）。</summary>
    internal System.Collections.Concurrent.ConcurrentDictionary<string, string> SessionEmittedForTest => _sessionEmitted;

    // ── IDisplayTextRestorer（v1.0.9：显示层一次性还原） ─────────────────

    /// <summary>还原文本中的脱敏标记（插件级共享映射——任意壳/任意轮次/跨进程的标签均可还原）。
    /// 未初始化时原样返回；映射为空时 UnmaskText 自然无变更。</summary>
    public string RestoreDisplayText(string text)
    {
        if (_engine is null || string.IsNullOrEmpty(text))
        {
            return text;
        }
        return _engine.UnmaskText(text, _sessionEmitted, _sessionEscapes);
    }

    // ── ISettingsContributor ─────────────────────────────────────────────

    public string SettingsSchemaJson()
    {
        // 识别类别开关（T5.1）：15 类正则类别，各一 toggle，扁平键 privacy.categories.<NAME>
        var catItems = new List<object>();
        foreach (var cat in PrivacyCategories.All)
        {
            catItems.Add(new
            {
                key = $"privacy.categories.{cat}",
                type = "toggle",
                title = $"ui.plugins.privacy.cat.{cat.ToLowerInvariant()}",
            });
        }

        return JsonSerializer.Serialize(new
        {
            groups = new object[]
            {
                new
                {
                    title = "ui.plugins.privacy.basic",
                    items = (object[])
                    [
                        new { key = "privacy.enabled", type = "toggle", title = "ui.plugins.privacy.master" },
                        new { key = "privacy.ner.enabled", type = "toggle", title = "ui.plugins.privacy.ner.toggle" },
                        new { key = "privacy.ner.threshold", type = "slider", title = "ui.plugins.privacy.threshold",
                              min = 0.6, max = 0.95, step = 0.05 },
                        new { key = "privacy.mask_note", type = "toggle", title = "ui.plugins.privacy.mask.note" },
                        new { key = "privacy.fail_open", type = "toggle", title = "ui.plugins.privacy.fail.open" },
                    ],
                },
                new
                {
                    title = "ui.plugins.privacy.categories.section",
                    items = catItems.ToArray(),
                },
                new
                {
                    title = "ui.plugins.privacy.egress.section",
                    items = (object[])
                    [
                        new { type = "note", title = "ui.plugins.privacy.egress.note1" },
                        new { type = "note", title = "ui.plugins.privacy.egress.note2" },
                    ],
                },
                new
                {
                    title = "ui.plugins.privacy.model",
                    items = (object[])
                    [
                        // 单模型部署（v1.0.12）：下拉移除，识别模型固定为裁决 A 默认档
                        new { type = "model", title = "ui.plugins.privacy.model", name = DefaultModel,
                              installAction = "install-model", checkAction = "run-self-check",
                              readyAction = "model-ready" },
                        new { key = "privacy.auto_register_limit", type = "slider",
                              title = "ui.plugins.privacy.auto.limit", min = 0, max = 2000, step = 50 },
                    ],
                },
            },
        });
    }

    public string GetSettingsJson()
    {
        if (_store is null || !_initialized)
        {
            return _host?.Settings.GetSettingsJson() ?? "{}";
        }
        var dict = new Dictionary<string, object?>
        {
            ["privacy.enabled"] = _store.Snapshot.MasterEnabled,
            ["privacy.ner.enabled"] = _store.Snapshot.NerEnabled,
            ["privacy.ner.model"] = _store.Snapshot.NerModel,
            ["privacy.ner.threshold"] = _store.Snapshot.NerThreshold,
            ["privacy.fail_open"] = _store.Snapshot.FailOpen,
            ["privacy.mask_note"] = _store.Snapshot.MaskNote,
            ["privacy.auto_register_limit"] = (double)_store.Snapshot.AutoRegisterLimit,
        };
        foreach (var (cat, on) in _store.Snapshot.Categories)
        {
            dict[$"privacy.categories.{cat}"] = on;
        }
        return JsonSerializer.Serialize(dict);
    }

    /// <summary>写回：真源 = 宿主 state.json（同步，快）；PrivacyStore 同步异步排队（真异步，
    /// 不在调用线程做任何同步等待）。总开关变化会改变 Decorate 的形态（装饰 vs 直通）
    /// → 请求宿主重建管线（热生效）。</summary>
    public void SetSettingsJson(string json)
    {
        if (!_initialized || _store is null)
        {
            return;
        }
        var masterBefore = _store.Snapshot.MasterEnabled;
        _host?.Settings.SetSettingsJson(json);

        // 隐私关键键（总开关）：同步落库，杜绝"开关刚打开的头一个请求 Decorate 读到旧值 → 不遮罩"的泄露窗口。
        // 此处同步等待安全：死锁根源（Changed→同步 Refresh）已改异步排队，门闩占用仅毫秒级 SQLite 写。
        // 调用方约定：SetSettingsJson 经 Task.Run 在后台线程调用（对话框/宿主均如此），不得在 UI 线程直调。
        var containsMaster = json.Contains("privacy.enabled", StringComparison.Ordinal);
        if (containsMaster)
        {
            SyncSettingsFromHostAsync().GetAwaiter().GetResult();
            if (_store.Snapshot.MasterEnabled != masterBefore)
            {
                _host?.RequestPipelineRebuild();   // 同步已完成 → 重建后 Decorate 必见新值
            }
            return;
        }

        // 其余键异步排队合并；管线重建必须等同步完成（否则 Decorate 读到旧值）
        QueueSettingsSync(masterChanged: ParseMaster(_host.Settings.GetSettingsJson()) != masterBefore);
    }

    /// <summary>设置同步排队（合并；与快照刷新排队同型——杜绝同步等待门闩）。
    /// masterChanged 时在同步完成后请求管线重建：Decorate 读到的一定是最新 MasterEnabled。</summary>
    private void QueueSettingsSync(bool masterChanged)
    {
        if (Interlocked.Exchange(ref _settingsSyncQueued, 1) == 1)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref _settingsSyncQueued, 0);
                    await SyncSettingsFromHostAsync();
                }
                while (Interlocked.Exchange(ref _settingsSyncQueued, 0) == 1);
                if (masterChanged)
                {
                    _host?.RequestPipelineRebuild();   // 同步已落库 → 重建后 Decorate 必见新值
                }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _settingsSyncQueued, 0);
                _host?.Log($"设置同步失败：{ex.Message}");
            }
        });
    }

    private static bool ParseMaster(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("privacy.enabled", out var v) &&
                   v.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public IReadOnlyList<string> Actions =>
    [
        "install-model", "run-self-check", "model-ready",
        "list-terms", "add-term", "remove-term", "set-term-enabled", "add-alias", "list-aliases",
        "list-whitelist", "add-whitelist", "remove-whitelist",
        "export-csv", "import-csv",
    ];

    /// <summary>当前生效模型：设置真源中用户所选，缺省回落裁决 A 默认档。</summary>
    private string SelectedModel =>
        _store?.Snapshot.NerModel is { Length: > 0 } m ? m : DefaultModel;

    /// <summary>
    /// 具名操作（argumentsJson 为操作参数 JSON）：
    /// install-model（默认档下载+自检）/ run-self-check / list-terms / add-term{category,term,aliases,note} / remove-term{category,term}。
    /// </summary>
    public async Task<PluginActionResult> RunActionAsync(string action, string? argumentsJson = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var models = _models ?? throw new InvalidOperationException("插件未初始化");
        var store = _store ?? throw new InvalidOperationException("插件未初始化");
        var model = SelectedModel;
        switch (action)
        {
            case "install-model":
                progress?.Report(L($"下载模型三件套（{model}：模型/词表/标签）…",
                    $"Downloading model set ({model}: model/vocab/labels)…",
                    $"Mengunduh set model ({model}: model/vocab/labels)…"));
                var install = await models.InstallAsync(model, cancellationToken, progress);
                if (!install.Success)
                {
                    return new PluginActionResult(false, install.Message);
                }
                progress?.Report(L("自动环境自检（会话加载 → 预热推理）…",
                    "Auto self-check (session load → warm-up inference)…",
                    "Self-check otomatis (muat sesi → inferensi pemanasan)…"));
                break;

            case "run-self-check":
                break;

            case "model-ready":
            {
                var ready = _models.IsModelReady(model);
                return new PluginActionResult(ready, model);
            }

            case "list-terms":
            {
                var all = await store.ListEntitiesAsync(includeDisabled: true);
                var terms = all.Select(e => new
                {
                    e.Id,
                    e.Category,
                    term = e.Canonical,
                    e.PlaceholderNo,
                    e.Enabled,
                    e.Source,
                    e.UseCount,
                }).ToList();
                var json = JsonSerializer.Serialize(terms, ActionJsonOpts);
                return new PluginActionResult(true, json);
            }

            case "add-term":
            {
                if (string.IsNullOrEmpty(argumentsJson))
                {
                    return new PluginActionResult(false, L("缺少参数", "Missing arguments", "Parameter tidak ada"));
                }
                using var doc = JsonDocument.Parse(argumentsJson);
                var category = doc.RootElement.GetProperty("category").GetString() ?? "";
                var term = doc.RootElement.GetProperty("term").GetString() ?? "";
                var aliases = doc.RootElement.TryGetProperty("aliases", out var al)
                    ? al.EnumerateArray().Select(a => a.GetString() ?? "").Where(a => a.Length > 0).ToList()
                    : [];
                var note = doc.RootElement.TryGetProperty("note", out var nt) ? nt.GetString() : null;
                try
                {
                    await store.AddEntityAsync(category, term, aliases, note);
                    return new PluginActionResult(true, L($"已添加 {term}", $"Added {term}", $"Ditambahkan {term}"));
                }
                catch (PrivacyConflictException ex)
                {
                    return new PluginActionResult(false, ex.Message);
                }
            }

            case "remove-term":
            {
                if (string.IsNullOrEmpty(argumentsJson))
                {
                    return new PluginActionResult(false, L("缺少参数", "Missing arguments", "Parameter tidak ada"));
                }
                using var doc = JsonDocument.Parse(argumentsJson);
                var category = doc.RootElement.GetProperty("category").GetString() ?? "";
                var term = doc.RootElement.GetProperty("term").GetString() ?? "";
                var all = await store.ListEntitiesAsync(category, term, includeDisabled: true);
                foreach (var e in all)
                {
                    await store.DeleteEntityAsync(e.Id);
                }
                return all.Count > 0
                    ? new PluginActionResult(true, L($"已删除 {term}", $"Removed {term}", $"Dihapus {term}"))
                    : new PluginActionResult(false, L($"未找到 {term}", $"Not found: {term}", $"Tidak ditemukan {term}"));
            }

            case "set-term-enabled":
            {
                var (id, enabled2) = ReadArgs(argumentsJson, "id", "enabled");
                await store.SetEntityEnabledAsync(id, enabled2);
                return new PluginActionResult(true, L("已更新", "Updated", "Diperbarui"));
            }

            case "add-alias":
            {
                var (id, alias) = ReadArgs2(argumentsJson, "id", "alias");
                if (string.IsNullOrWhiteSpace(alias))
                {
                    return new PluginActionResult(false, L("别名不能为空", "Alias is empty", "Alias kosong"));
                }
                await store.AddAliasAsync(id, alias.Trim());
                return new PluginActionResult(true, L($"已添加别名 {alias.Trim()}", $"Alias added: {alias.Trim()}",
                    $"Alias ditambahkan: {alias.Trim()}"));
            }

            case "list-aliases":
            {
                var id = long.Parse(JsonDocument.Parse(argumentsJson ?? "{}").RootElement.GetProperty("id").GetRawText());
                var aliases = await store.ListAliasesAsync(id);
                return new PluginActionResult(true, JsonSerializer.Serialize(aliases.Select(a => new
                {
                    a.Id,
                    a.Alias,
                })));
            }

            case "list-whitelist":
            {
                var whitelist = await store.ListWhitelistAsync();
                return new PluginActionResult(true, JsonSerializer.Serialize(whitelist.Select(w => new
                {
                    w.Id,
                    w.Pattern,
                    w.Kind,
                    w.Note,
                }), ActionJsonOpts));
            }

            case "add-whitelist":
            {
                using var doc = JsonDocument.Parse(argumentsJson ?? "{}");
                var pattern = doc.RootElement.GetProperty("pattern").GetString() ?? "";
                var kind = doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : "literal";
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    return new PluginActionResult(false, L("规则不能为空", "Pattern is empty", "Pola kosong"));
                }
                var added = await store.AddWhitelistAsync(pattern.Trim(), kind ?? "literal");
                return new PluginActionResult(true, L($"已加入白名单 #{added.Id}", $"Whitelisted #{added.Id}",
                    $"Didaftarkan #{added.Id}"));
            }

            case "remove-whitelist":
            {
                using var doc = JsonDocument.Parse(argumentsJson ?? "{}");
                var wid = doc.RootElement.GetProperty("id").GetInt64();
                await store.RemoveWhitelistAsync(wid);
                return new PluginActionResult(true, L("已移除", "Removed", "Dihapus"));
            }

            case "export-csv":
            {
                var sw = new StringWriter();
                await store.ExportCsvAsync(sw);
                return new PluginActionResult(true, sw.ToString());
            }

            case "import-csv":
            {
                using var doc = JsonDocument.Parse(argumentsJson ?? "{}");
                var csv = doc.RootElement.GetProperty("csv").GetString() ?? "";
                var result = await store.ImportCsvAsync(new StringReader(csv));
                return new PluginActionResult(true, JsonSerializer.Serialize(new
                {
                    imported = result.Imported,
                    skipped = result.Skipped,
                }));
            }

            default:
                return new PluginActionResult(false, L($"未知操作：{action}", $"Unknown action: {action}",
                    $"Aksi tidak dikenal: {action}"));
        }

        var check = models.RunSelfCheck(model);
        return check.Success
            ? new PluginActionResult(true, L($"自检通过（冷加载 {check.ColdLoadMs} ms，预热 {check.WarmDetectMs} ms）",
                $"Self-check passed (cold load {check.ColdLoadMs} ms, warm detect {check.WarmDetectMs} ms)",
                $"Self-check lulus (cold load {check.ColdLoadMs} ms, warm detect {check.WarmDetectMs} ms)"))
            : new PluginActionResult(false, L($"自检失败：{check.Message}", $"Self-check failed: {check.Message}",
                $"Self-check gagal: {check.Message}"));
    }

    // ── 内部 ─────────────────────────────────────────────────────────────

    /// <summary>读 JSON 参数（bool）。</summary>
    private static (long Id, bool Enabled) ReadArgs(string? json, string idKey, string enabledKey)
    {
        using var doc = JsonDocument.Parse(json ?? "{}");
        return (doc.RootElement.GetProperty(idKey).GetInt64(),
            doc.RootElement.GetProperty(enabledKey).GetBoolean());
    }

    /// <summary>读 JSON 参数（long + string）。</summary>
    private static (long Id, string? Value) ReadArgs2(string? json, string idKey, string valueKey)
    {
        using var doc = JsonDocument.Parse(json ?? "{}");
        return (doc.RootElement.GetProperty(idKey).GetInt64(),
            doc.RootElement.TryGetProperty(valueKey, out var v) ? v.GetString() : null);
    }

    /// <summary>按宿主语言取文案（插件无 I18n 依赖，三语内联）。</summary>
    private string L(string zh, string en, string id) => _host?.Language switch
    {
        "en" => en,
        "id" => id,
        _ => zh,
    };

    private void OnEngineChanged()
    {
        // 死锁教训（Windows 实测"思考"永久卡住）：store 写持门闩 → ReloadSnapshot → Changed
        // → 同步 RefreshAsync → 同一门闩 WaitAsync → 互等。必须丢到线程池异步排队：
        // 写者先出闩，排队刷新随后拿闩；连续变更（滑块拖动/批量写）合并为一轮。
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref _refreshQueued, 0);
                    await _engine!.RefreshAsync();
                }
                while (Interlocked.Exchange(ref _refreshQueued, 0) == 1);   // 排队期间又有变更 → 再刷一轮
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                _host?.Log($"快照刷新失败：{ex.Message}");
            }
        });
    }

    /// <summary>宿主设置（真源）→ PrivacyStore（引擎快照源）单向同步。真异步：全程 await，
    /// 不在任何线程同步等待门闩（死锁教训）。</summary>
    private async Task SyncSettingsFromHostAsync()
    {
        if (_store is null || _host is null)
        {
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(_host.Settings.GetSettingsJson());
            var root = doc.RootElement;

            if (root.TryGetProperty("privacy.enabled", out var enabled))
            {
                await _store.SetSettingAsync("privacy.enabled",
                    enabled.ValueKind == JsonValueKind.True ? "1" : "0");
            }
            if (root.TryGetProperty("privacy.ner.enabled", out var nerEnabled))
            {
                await _store.SetSettingAsync("privacy.ner.enabled",
                    nerEnabled.ValueKind == JsonValueKind.True ? "1" : "0");
            }
            if (root.TryGetProperty("privacy.ner.model", out var model) && model.ValueKind == JsonValueKind.String)
            {
                await _store.SetSettingAsync("privacy.ner.model", model.GetString() ?? "");
            }
            if (root.TryGetProperty("privacy.ner.threshold", out var threshold) && threshold.ValueKind == JsonValueKind.Number)
            {
                await _store.SetSettingAsync("privacy.ner.threshold",
                    threshold.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            // T5.1 扩展键：类别开关（扁平键 → 合并为一个 JSON dict）/ 失败模式 / 说明注入 / 出网策略覆写
            var catKeys = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().Where(p => p.Name.StartsWith("privacy.categories.", StringComparison.Ordinal)).ToList()
                : [];
            if (catKeys.Count > 0)
            {
                var dict = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var p in catKeys)
                {
                    dict[p.Name["privacy.categories.".Length..]] = p.Value.ValueKind == JsonValueKind.True;
                }
                await _store.SetSettingAsync("privacy.categories", JsonSerializer.Serialize(dict));
            }
            if (root.TryGetProperty("privacy.fail_open", out var failOpen))
            {
                await _store.SetSettingAsync("privacy.fail_open", failOpen.ValueKind == JsonValueKind.True ? "1" : "0");
            }
            if (root.TryGetProperty("privacy.mask_note", out var maskNote))
            {
                await _store.SetSettingAsync("privacy.mask_note", maskNote.ValueKind == JsonValueKind.True ? "1" : "0");
            }
            if (root.TryGetProperty("privacy.egress_policy", out var egress) && egress.ValueKind == JsonValueKind.String)
            {
                await _store.SetSettingAsync("privacy.egress_policy", egress.GetString() ?? "{}");
            }
            if (root.TryGetProperty("privacy.auto_register_limit", out var autoLimit) && autoLimit.ValueKind == JsonValueKind.Number)
            {
                await _store.SetSettingAsync("privacy.auto_register_limit",
                    ((int)autoLimit.GetDouble()).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        catch (JsonException)
        {
            // 宿主设置损坏：按默认值继续（引擎已有兜底）
        }
    }
}
