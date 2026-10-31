using System.Text.RegularExpressions;
using AgentBrook.Privacy.Detectors;
using AgentBrook.Privacy.Ner;

namespace AgentBrook.Privacy;

/// <summary>
/// IPrivacyEngine 进程内实现（无会话集合状态，v4/v5）：组合注册表 + 识别器 + 白名单 + 出站策略 + Store。
/// 会话集合（emittedMap / escapeMap）由宿主中间件壳持有，**既作出站判定的入参、又作入站还原的唯一依据**；
/// 还原不查 Snapshot（回退即全表反查 = 重开枚举通道，§14 风险 13 不变量）。
/// 词典 / 设置变更后由宿主调用 <see cref="RefreshAsync"/>（Store.Changed 事件驱动）。
/// </summary>
public sealed class PrivacyEngine : IPrivacyEngine, IAsyncDisposable
{
    /// <summary>占位符还原（入站）：半角宽容文法（与 Mask 侧一致，D16 文法 \d{1,6}）；全角形永不参与（v4 中性形不变量）。</summary>
    private static readonly Regex TagRx =
        new(@"\[\s*(?<c>[A-Za-z_][A-Za-z0-9_]{0,30}?)\s*[_\-\s]?\s*(?<n>\d{1,6})\s*\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly PrivacyStore _store;
    private readonly EgressPolicy _egress;
    private readonly int _autoRegisterLimit;
    private readonly IReadOnlyList<IRecognizer> _extraRecognizers;
    private readonly Action _forwardChanged;
    private readonly Dictionary<string, (int Low, int High)> _ownedRanges = new();
    private PlaceholderRegistry _registry;
    private PrivacyMasker _masker;

    private PrivacyEngine(PrivacyStore store, PlaceholderRegistry registry, PrivacyMasker masker,
        EgressPolicy egress, int autoRegisterLimit, IReadOnlyList<IRecognizer> extraRecognizers)
    {
        _store = store;
        _registry = registry;
        _masker = masker;
        _egress = egress;
        _autoRegisterLimit = autoRegisterLimit;
        _extraRecognizers = extraRecognizers;
        _forwardChanged = () => Changed?.Invoke();
        _store.Changed += _forwardChanged;
    }

    /// <summary>从 Store 组装引擎：载入词典快照、为全部类别保留初始号段、构建识别器集合。</summary>
    public static async Task<PrivacyEngine> CreateAsync(PrivacyStore store,
        IEnumerable<IRecognizer>? extraRecognizers = null,
        int autoRegisterLimit = 200,
        IReadOnlyDictionary<string, string>? mcpTransports = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var persisted = await store.ListEntitiesAsync(includeDisabled: true);
        var glossary = await store.LoadGlossaryAsync();
        var whitelist = await store.ListWhitelistAsync();
        var snapshot = store.Snapshot;
        var extra = extraRecognizers?.ToList() ?? [];

        var registry = new PlaceholderRegistry(persisted, autoRegisterLimit);
        var owned = new Dictionary<string, (int Low, int High)>();
        foreach (var category in PrivacyCategories.All)
        {
            var upper = await store.ReserveSeatAsync(category, 64);
            var low = Math.Max(1, upper - 63);
            owned[category] = (low, upper);
            registry.AddOwnedRange(category, low, upper);
        }

        var recognizers = BuildRecognizers(store, snapshot, glossary, extra);

        var egress = new EgressPolicy(snapshot.EgressPolicy, mcpTransports);
        var engine = new PrivacyEngine(store, registry,
            new PrivacyMasker(registry, recognizers, new WhitelistFilter(whitelist)),
            egress, autoRegisterLimit, extra);
        foreach (var (category, range) in owned)
        {
            engine._ownedRanges[category] = range;
        }
        return engine;
    }

    public PrivacySnapshot Snapshot => _store.Snapshot;

    /// <summary>快照版本变化（转发 Store.Changed；宿主收到后调用 <see cref="RefreshAsync"/>）。</summary>
    public event Action? Changed;

    public MaskOutcome MaskText(string text,
        IReadOnlyDictionary<string, string> emittedMap, IReadOnlyDictionary<string, string> escapeMap) =>
        _masker.Mask(text, emittedMap, escapeMap);

    /// <summary>工具实参出站脱敏：JSON 递归（含属性名 key），每个字符串走与文本相同的区段分类（含转义）。</summary>
    public ArgsMaskOutcome MaskToolArgs(object? args,
        IReadOnlyDictionary<string, string> emittedMap, IReadOnlyDictionary<string, string> escapeMap)
    {
        var emitted = new Dictionary<string, string>(StringComparer.Ordinal);
        var escapes = new Dictionary<string, string>(StringComparer.Ordinal);
        var (value, _) = PrivacyJson.Transform(args, s =>
        {
            var outcome = _masker.Mask(s, emittedMap, escapeMap);
            foreach (var (k, v) in outcome.Emitted)
            {
                emitted.TryAdd(k, v);
            }
            foreach (var (k, v) in outcome.Escapes)
            {
                escapes.TryAdd(k, v);
            }
            return outcome.Text;
        });
        return new ArgsMaskOutcome(value, emitted, escapes);
    }

    /// <summary>
    /// 入站还原（文本）：先占位符（仅 emittedMap 内标签）后 escapeMap 精确还原——
    /// 顺序不可颠倒：escape 替换结果不再扫描，防止用户原字面回流后被二次命中（TOCTOU）。
    /// </summary>
    public string UnmaskText(string text,
        IReadOnlyDictionary<string, string> emittedMap, IReadOnlyDictionary<string, string> escapeMap) =>
        UnmaskCore(text, emittedMap, escapeMap);

    /// <summary>
    /// 工具实参还原：按 EgressPolicy 两级匹配决定 restore / keep；
    /// restore 仅查宿主传入的 emittedMap + escapeMap——**不查 Snapshot**（防枚举双通道不变量）。
    /// </summary>
    public object? RestoreToolArgs(object? args, string toolName,
        IReadOnlyDictionary<string, string> emittedMap, IReadOnlyDictionary<string, string> escapeMap)
    {
        if (_egress.Decide(toolName) == PolicyAction.Keep)
        {
            return args;
        }
        var (value, _) = PrivacyJson.Transform(args, s => UnmaskCore(s, emittedMap, escapeMap));
        return value;
    }

    public void BeginTurn() => _registry.BeginTurn();

    /// <summary>overlay 批量落库 + 统计写回（响应完成时由壳触发；幂等）。热路径零 SQL 的唯一写出口。</summary>
    public async Task FlushAsync()
    {
        var (entities, usage) = _registry.DrainPending();
        await _store.FlushOverlayAsync(entities, usage);
    }

    /// <summary>词典 / 设置变更后重建识别层：先排空 overlay（幂等），再采纳词典变更（含停用抑制与删除）。</summary>
    public async Task RefreshAsync()
    {
        await FlushAsync();
        var persisted = await _store.ListEntitiesAsync(includeDisabled: true);
        var glossary = await _store.LoadGlossaryAsync();
        var whitelist = await _store.ListWhitelistAsync();
        _registry.UpsertPersisted(persisted);
        _masker = new PrivacyMasker(_registry,
            BuildRecognizers(_store, _store.Snapshot, glossary, _extraRecognizers),
            new WhitelistFilter(whitelist));
    }

    /// <summary>识别器集合：正则（类别开关）+ 词典 AC +（快照启用且模型齐备时）本地 NER + 宿主扩展识别器。</summary>
    private static List<IRecognizer> BuildRecognizers(PrivacyStore store, PrivacySnapshot snapshot,
        IReadOnlyList<GlossaryEntry> glossary, IEnumerable<IRecognizer> extra)
    {
        var recognizers = new List<IRecognizer>
        {
            new RegexPiiDetector(snapshot.Categories),
            new GlossaryDetector(glossary),
        };
        // P2 本地 NER（17 号方案）：privacy.ner.enabled 且模型文件齐备时挂载；缺失 / 损坏 → fail-open 不挂载
        var modelsDir = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(store.DatabasePath))!, "models");
        var ner = NerRecognizer.CreateIfConfigured(snapshot, modelsDir);
        if (ner is not null)
        {
            recognizers.Add(ner);
        }
        recognizers.AddRange(extra);
        return recognizers;
    }

    private static string UnmaskCore(string text,
        IReadOnlyDictionary<string, string> emittedMap, IReadOnlyDictionary<string, string> escapeMap)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        var result = TagRx.Replace(text, m =>
        {
            var canonical = $"[{m.Groups["c"].Value.ToUpperInvariant()}_{m.Groups["n"].Value}]";
            return emittedMap.TryGetValue(canonical, out var value) ? value : m.Value;
        });
        foreach (var (escaped, original) in escapeMap)
        {
            result = result.Replace(escaped, original);
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        _store.Changed -= _forwardChanged;
        await ValueTask.CompletedTask;
    }
}
