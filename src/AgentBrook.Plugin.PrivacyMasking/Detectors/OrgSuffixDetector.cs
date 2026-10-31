using System.Text.RegularExpressions;

namespace AgentBrook.Privacy.Detectors;

/// <summary>
/// 企业名后缀启发式（§5.3，默认关闭、不参与脱敏）：只产出"候选建议"供设置页确认后入词典——
/// 启发式精度不足以静默替换，识别主路径不含此器。
/// </summary>
public sealed class OrgSuffixDetector
{
    private static readonly Regex Rx = new(
        @"[\u4e00-\u9fa5]{2,12}(?:股份有限公司|有限责任公司|有限公司|集团|科技|银行|医院|大学|研究院)",
        RegexOptions.Compiled);

    /// <summary>去重后的候选企业名列表（原文原样）。</summary>
    public IReadOnlyList<string> Suggest(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<string>? result = null;
        foreach (Match m in Rx.Matches(text))
        {
            if (seen.Add(m.Value))
            {
                result ??= [];
                result.Add(m.Value);
            }
        }
        return result ?? [];
    }
}
