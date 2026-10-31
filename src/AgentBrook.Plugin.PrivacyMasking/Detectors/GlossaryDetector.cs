namespace AgentBrook.Privacy.Detectors;

/// <summary>
/// 用户词典识别器（§5.2）：Aho-Corasick 多模式匹配，别名归一小写后建自动机；
/// ASCII 别名要求词边界（前后非 ASCII 字母数字）、CJK 别名子串命中即中；
/// 同一终位多模式取最长且过边界者；命中 Canonical 直接携带实体规范形。
/// </summary>
public sealed class GlossaryDetector : IRecognizer
{
    private sealed record Payload(string Category, string Canonical, bool BoundaryStart, bool BoundaryEnd);

    private readonly AhoCorasick<Payload> _ac = new();

    public GlossaryDetector(IReadOnlyList<GlossaryEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Normalized))
            {
                continue;
            }
            var first = entry.Normalized[0];
            var last = entry.Normalized[^1];
            _ac.Add(entry.Normalized, new Payload(
                entry.Category,
                entry.Canonical,
                char.IsAsciiLetterOrDigit(first),
                char.IsAsciiLetterOrDigit(last)));
        }
        _ac.Build();
    }

    public IReadOnlyList<PrivacyMatch> Detect(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        var lowered = new char[text.Length];   // 逐字符小写：长度 1:1，偏移不漂移
        for (var i = 0; i < text.Length; i++)
        {
            lowered[i] = char.ToLowerInvariant(text[i]);
        }
        var byEnd = new Dictionary<int, List<AhoCorasick<Payload>.AcHit>>();
        foreach (var hit in _ac.Scan(new string(lowered)))
        {
            if (!byEnd.TryGetValue(hit.End, out var list))
            {
                list = [];
                byEnd[hit.End] = list;
            }
            list.Add(hit);
        }
        if (byEnd.Count == 0)
        {
            return [];
        }
        List<PrivacyMatch>? matches = null;
        foreach (var (end, list) in byEnd)
        {
            list.Sort((a, b) => b.Length.CompareTo(a.Length));   // 最长优先
            foreach (var hit in list)
            {
                var start = end - hit.Length;
                if (!BoundaryOk(lowered, start, end, hit.Payload))
                {
                    continue;
                }
                matches ??= [];
                matches.Add(new PrivacyMatch(start, hit.Length, hit.Payload.Category, hit.Payload.Canonical, 95));
                break;
            }
        }
        // 按位置排序输出（确定性；Masker 侧另有全局贪心）
        return matches?.
            OrderBy(m => m.Start).
            ThenByDescending(m => m.Length).ToList() ?? [];
    }

    private static bool BoundaryOk(char[] lowered, int start, int end, Payload payload)
    {
        if (payload.BoundaryStart && start > 0 && IsAsciiAlnum(lowered[start - 1]))
        {
            return false;
        }
        if (payload.BoundaryEnd && end < lowered.Length && IsAsciiAlnum(lowered[end]))
        {
            return false;
        }
        return true;
    }

    private static bool IsAsciiAlnum(char c) =>
        c is >= 'a' and <= 'z' or >= '0' and <= '9';
}
