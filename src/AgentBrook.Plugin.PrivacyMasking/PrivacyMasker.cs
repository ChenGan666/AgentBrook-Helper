using System.Text;
using System.Text.RegularExpressions;
using AgentBrook.Privacy.Detectors;

namespace AgentBrook.Privacy;

/// <summary>
/// 出站脱敏器（15 号设计 §5.4 流水线 / §6.5 转义）：
/// ① 区段分类——emitted 半角占位符保护、形似占位符转义全角（已全角恒等）、白名单豁免；
/// ② 检测器识别（跳过保护段）；③ 起点升序 + 长度降序 + 优先级降序贪心取不重叠；
/// ④ 登记占位符并替换，ΔEmitted / ΔEscape 增量随结果返回（宿主壳累计成会话集合）。
/// 幂等前提：调用方把上次返回的增量并入传入映射（幂等状态在宿主侧，引擎无会话状态）。
/// </summary>
public sealed class PrivacyMasker
{
    /// <summary>半角占位符形（宽容文法：大小写 / _ - 空格分隔符；编号 \d{1,6} 与还原正则一致，D16）。</summary>
    private static readonly Regex HalfTagRx =
        new(@"\[\s*(?<c>[A-Za-z_][A-Za-z0-9_]{0,30}?)\s*[_\- ]?\s*(?<n>\d{1,6})\s*\]", RegexOptions.Compiled);

    /// <summary>全角占位符形（转义中性形）：仅作保护，永不参与占位符还原（v4 政策）。</summary>
    private static readonly Regex FullTagRx =
        new(@"[［]\s*[A-Za-z_][A-Za-z0-9_]{0,30}?\s*[_\- ]?\s*\d{1,6}\s*[］]", RegexOptions.Compiled);

    private readonly PlaceholderRegistry _registry;
    private readonly IReadOnlyList<IRecognizer> _recognizers;
    private readonly WhitelistFilter _whitelist;

    public PrivacyMasker(PlaceholderRegistry registry,
        IReadOnlyList<IRecognizer> recognizers,
        WhitelistFilter whitelist)
    {
        _registry = registry;
        _recognizers = recognizers;
        _whitelist = whitelist;
    }

    private readonly record struct Edit(int Start, int Length, string Replacement);

    public MaskOutcome Mask(string text,
        IReadOnlyDictionary<string, string> emittedMap,
        IReadOnlyDictionary<string, string> escapeMap)
    {
        if (string.IsNullOrEmpty(text))
        {
            return MaskOutcome.None with { Text = text };
        }
        var edits = new List<Edit>();
        var emitted = new Dictionary<string, string>(StringComparer.Ordinal);
        var escapes = new Dictionary<string, string>(StringComparer.Ordinal);

        // ── 1. 区段分类（互斥保护段） ──
        foreach (Match m in HalfTagRx.Matches(text))
        {
            var canonicalTag = $"[{m.Groups["c"].Value.ToUpperInvariant()}_{m.Groups["n"].Value}]";
            if (emittedMap.ContainsKey(canonicalTag))
            {
                edits.Add(new Edit(m.Index, m.Length, m.Value));   // 1a：本系统产出 → 原样保护（幂等）
                continue;
            }
            var escaped = m.Value.Replace('[', '［').Replace(']', '］');
            if (!escapeMap.ContainsKey(escaped))
            {
                escapes[escaped] = m.Value;                        // 1b：登记转义（宿主已有的不重复登记）
            }
            edits.Add(new Edit(m.Index, m.Length, escaped));
        }
        foreach (Match m in FullTagRx.Matches(text))
        {
            edits.Add(new Edit(m.Index, m.Length, m.Value));       // 已全角：恒等（中性形，跳过登记）
        }
        foreach (var (start, length) in _whitelist.FindRanges(text))
        {
            edits.Add(new Edit(start, length, text.Substring(start, length)));   // 1c 白名单豁免
        }
        var protectedRanges = edits.Select(e => (Start: e.Start, End: e.Start + e.Length)).ToList();

        // ── 2/3. 检测 + 过滤保护段 + 贪心不重叠 ──
        // 贪心键（17 号 §3.3）：Priority 降序 → 起点升序 → 长度降序——高层 span 先占位，
        // 低层与已占位重叠的整段丢弃（避免 NER 长 span 吞掉内部高精度规则命中）
        var candidates = new List<PrivacyMatch>();
        foreach (var recognizer in _recognizers)
        {
            candidates.AddRange(recognizer.Detect(text));
        }
        var ordered = candidates
            .Where(m => !protectedRanges.Any(p => m.Start < p.End && p.Start < m.Start + m.Length))
            .OrderByDescending(m => m.Priority)
            .ThenBy(m => m.Start)
            .ThenByDescending(m => m.Length);
        var taken = new List<PrivacyMatch>();
        foreach (var match in ordered)
        {
            if (taken.Any(t => match.Start < t.Start + t.Length && t.Start < match.Start + match.Length))
            {
                continue;
            }
            taken.Add(match);
        }

        // ── 4. 登记占位符并替换 ──
        foreach (var match in taken)
        {
            var span = text.Substring(match.Start, match.Length);
            var canonical = match.Canonical ?? PrivacyNormalizer.Canonicalize(match.Category, span);
            var allocation = _registry.GetOrRegister(match.Category, canonical, match.AutoFill);
            if (allocation is null)
            {
                continue;   // 停用实体：不再脱敏
            }
            edits.Add(new Edit(match.Start, match.Length, allocation.Tag));
            emitted.TryAdd(allocation.Tag, canonical);
        }

        // 应用全部编辑（右到左，偏移不失效）
        edits.Sort((a, b) => b.Start.CompareTo(a.Start));
        var sb = new StringBuilder(text);
        foreach (var edit in edits)
        {
            sb.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
        }
        return new MaskOutcome(sb.ToString(), emitted.Count, emitted, escapes);
    }
}
