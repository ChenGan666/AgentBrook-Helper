using System.Text.RegularExpressions;

namespace AgentBrook.Privacy.Detectors;

/// <summary>
/// 结构化 PII 正则识别器（15 号设计 §5.1）：11 类规则 + 校验位复算（Luhn / MOD 11-2 / MOD 31）。
/// 校验位复算通过才产出命中（压误报）；规则开关来自快照 privacy.categories（缺省按规则默认）。
/// </summary>
public sealed class RegexPiiDetector : IRecognizer
{
    private sealed record Rule(string Key, string Category, int Priority, Regex Rx, Func<string, bool>? Validate);

    private static bool On(string key) => key is
        "PHONE" or "EMAIL" or "IDCARD" or "BANKCARD" or "USCC";

    private static readonly Rule[] AllRules =
    [
        new("PHONE", PrivacyCategories.Phone, 80,
            // 兼容分隔写法（138-1234-5678 / 138 1234 5678）；环视防长数字误切（§5.1）
            new Regex(@"(?<!\d)1[3-9]\d(?:[-\s]?\d{4}){2}(?!\d)", RegexOptions.Compiled), null),
        new("EMAIL", PrivacyCategories.Email, 80,
            new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled), null),
        new("IDCARD", PrivacyCategories.IdCard, 90,
            new Regex(@"(?<![0-9Xx])[1-9]\d{5}(?:18|19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])\d{3}[\dXx](?![0-9Xx])",
                RegexOptions.Compiled), IsValidIdCard),
        new("BANKCARD", PrivacyCategories.BankCard, 50,
            new Regex(@"(?<!\d)\d{16,19}(?!\d)", RegexOptions.Compiled), LuhnValid),
        new("USCC", PrivacyCategories.Uscc, 90,
            new Regex(@"(?<![0-9A-Z])[0-9A-HJ-NPQRTUWXY]{2}\d{6}[0-9A-HJ-NPQRTUWXY]{10}(?![0-9A-Z])",
                RegexOptions.Compiled), IsValidUscc),
        new("LANDLINE", PrivacyCategories.Phone, 40,
            new Regex(@"(?<!\d)0\d{2,3}[-\s]?\d{7,8}(?!\d)", RegexOptions.Compiled), null),
        new("PLATE", PrivacyCategories.Plate, 60,
            new Regex(@"(?<![A-Za-z0-9])[京津沪渝冀晋蒙辽吉黑苏浙皖闽赣鲁豫鄂湘粤桂琼川贵云藏陕甘青宁新使领][A-HJ-NP-Z][A-HJ-NP-Z0-9]{4,5}[A-HJ-NP-Z0-9挂学警港澳]?(?![A-Za-z0-9])",
                RegexOptions.Compiled), null),
        new("IP", PrivacyCategories.Ip, 60,
            new Regex(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![\d.])",
                RegexOptions.Compiled), IsPublicIpv4),
        new("URL", PrivacyCategories.Url, 60,
            new Regex(@"(?:https?://|www\.)[^\s<>""')\]]{2,}", RegexOptions.Compiled), null),
        new("PASSPORT", PrivacyCategories.Passport, 60,
            new Regex(@"(?<![A-Za-z0-9])[EGeg]\d{8}(?![A-Za-z0-9])", RegexOptions.Compiled), null),
        new("SECRET_ASSIGN", PrivacyCategories.Custom, 60,
            new Regex(@"(?i)\b(?:password|passwd|pwd|secret|api[_-]?key|token)\s*[=:]\s*\S+", RegexOptions.Compiled), null),
    ];

    private readonly List<Rule> _active;

    public RegexPiiDetector(IReadOnlyDictionary<string, bool> categorySwitches)
    {
        _active = [];
        foreach (var rule in AllRules)
        {
            var enabled = categorySwitches.TryGetValue(rule.Key, out var on) ? on : On(rule.Key);
            if (enabled)
            {
                _active.Add(rule);
            }
        }
    }

    public IReadOnlyList<PrivacyMatch> Detect(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        List<PrivacyMatch>? matches = null;
        foreach (var rule in _active)
        {
            foreach (Match m in rule.Rx.Matches(text))
            {
                if (m.Length == 0)
                {
                    continue;
                }
                if (rule.Validate is not null && !rule.Validate(m.Value))
                {
                    continue;
                }
                matches ??= [];
                matches.Add(new PrivacyMatch(m.Index, m.Length, rule.Category, null, rule.Priority));
            }
        }
        return matches ?? [];
    }

    /// <summary>身份证校验位：GB 11643 MOD 11-2。</summary>
    private static bool IsValidIdCard(string s)
    {
        if (s.Length != 18)
        {
            return false;
        }
        int[] w = [7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2];
        char[] map = ['1', '0', 'X', '9', '8', '7', '6', '5', '4', '3', '2'];
        var sum = 0;
        for (var i = 0; i < 17; i++)
        {
            sum += (s[i] - '0') * w[i];
        }
        return char.ToUpperInvariant(s[17]) == map[sum % 11];
    }

    /// <summary>银行卡 Luhn 校验。</summary>
    private static bool LuhnValid(string digits)
    {
        var sum = 0;
        var dbl = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (dbl)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }
            sum += d;
            dbl = !dbl;
        }
        return sum % 10 == 0;
    }

    private const string UsccCharset = "0123456789ABCDEFGHJKLMNPQRTUWXY";

    /// <summary>统一社会信用代码校验位：GB 32100 MOD 31。</summary>
    private static bool IsValidUscc(string s)
    {
        if (s.Length != 18)
        {
            return false;
        }
        int[] w = [1, 3, 9, 27, 19, 26, 16, 17, 20, 29, 25, 13, 8, 24, 10, 30, 28];
        var sum = 0;
        for (var i = 0; i < 17; i++)
        {
            sum += UsccCharset.IndexOf(s[i]) * w[i];
        }
        var mod = sum % 31;
        var expect = mod == 0 ? '0' : UsccCharset[31 - mod];
        return s[17] == expect;
    }

    /// <summary>IPv4：正则已保证段值合法，这里过滤私网 / 环回 / 链路本地（§5.1 白名单豁免）。</summary>
    private static bool IsPublicIpv4(string s)
    {
        var parts = s.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }
        var b = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            if (!byte.TryParse(parts[i], out var v))
            {
                return false;
            }
            b[i] = v;
        }
        if (b[0] is 0 or 10 or 127)
        {
            return false;
        }
        if (b[0] == 192 && b[1] == 168)
        {
            return false;
        }
        if (b[0] == 172 && b[1] is >= 16 and <= 31)
        {
            return false;
        }
        if (b[0] == 169 && b[1] == 254)
        {
            return false;
        }
        return true;
    }
}
