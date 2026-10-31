using System.Text.RegularExpressions;

namespace AgentBrook.Privacy.Detectors;

/// <summary>白名单过滤器（§5.4 步骤 1c）：literal 大小写不敏感查找、regex 带 200ms 超时；命中区段豁免脱敏。</summary>
public sealed class WhitelistFilter
{
    private readonly List<string> _literals = [];
    private readonly List<Regex> _regexes = [];

    public WhitelistFilter(IReadOnlyList<WhitelistEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Kind == "literal")
            {
                _literals.Add(entry.Pattern);
            }
            else
            {
                try
                {
                    _regexes.Add(new Regex(entry.Pattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(200)));
                }
                catch (ArgumentException)
                {
                    // 非法正则条目忽略（存储端未校验，过滤端容错）
                }
            }
        }
    }

    public List<(int Start, int Length)> FindRanges(string text)
    {
        var ranges = new List<(int Start, int Length)>();
        foreach (var literal in _literals)
        {
            var idx = 0;
            while ((idx = text.IndexOf(literal, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                ranges.Add((idx, literal.Length));
                idx += literal.Length;
            }
        }
        foreach (var rx in _regexes)
        {
            foreach (Match m in rx.Matches(text))
            {
                if (m.Length > 0)
                {
                    ranges.Add((m.Index, m.Length));
                }
            }
        }
        return ranges;
    }
}
