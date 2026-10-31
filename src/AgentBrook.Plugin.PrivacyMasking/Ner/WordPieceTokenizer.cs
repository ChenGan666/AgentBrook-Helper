using System.Text;

namespace AgentBrook.Privacy.Ner;

/// <summary>
/// BERT WordPiece 分词器（17 号风险 2：offset 错位 = 替换错字——自研实现确保偏移确定性与可测性，
/// 不引入第三方分词包）。预处理对齐 BERT BasicTokenizer：小写化、全角→半角、空白切分、
/// CJK 逐字、标点逐字；词内最长匹配 WordPiece，任何 piece 缺失整词 [UNK]（BERT 标准）。
/// 同一词的多个 piece 共享该词的原文偏移（实体 span = 词级并集，emoji 等 UTF-16 代理对长度保真）。
/// </summary>
public sealed class WordPieceTokenizer
{
    public const string UnkToken = "[UNK]";
    public const string ClsToken = "[CLS]";
    public const string SepToken = "[SEP]";

    /// <summary>分词结果：vocab id、piece 表面形、原文偏移（词级）。</summary>
    public sealed record Token(int Id, string Surface, int Start, int Length);

    private readonly Dictionary<string, int> _vocab;

    public WordPieceTokenizer(string vocabPath)
    {
        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(vocabPath))
        {
            var token = raw.TrimEnd('\r');
            if (token.Length == 0 || _vocab.ContainsKey(token))
            {
                continue;
            }
            _vocab[token] = _vocab.Count;
        }
    }

    public int VocabSize => _vocab.Count;

    public int UnkId => IdOf(UnkToken);
    public int ClsId => IdOf(ClsToken);
    public int SepId => IdOf(SepToken);

    public int IdOf(string token) => _vocab.TryGetValue(token, out var id) ? id : 0;

    /// <summary>正文分词（不含 CLS / SEP）；超过 maxTokens 截断（调用方负责滑窗）。</summary>
    public IReadOnlyList<Token> Tokenize(string text, int maxTokens = 510)
    {
        var result = new List<Token>();
        foreach (var (word, start, length) in BasicSplit(text))
        {
            foreach (var piece in WordPiece(word, start, length))
            {
                result.Add(piece);
                if (result.Count >= maxTokens)
                {
                    return result;
                }
            }
        }
        return result;
    }

    private readonly record struct Word(StringBuilder Text, int Start, int Length);

    private static IEnumerable<(StringBuilder Word, int Start, int Length)> BasicSplit(string text)
    {
        var sb = new StringBuilder();
        var start = -1;
        var i = 0;
        while (i < text.Length)
        {
            var c = CleanupChar(text[i]);
            // UTF-16 代理对（emoji 等）成对消费，词长保真
            var charLen = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    yield return (sb, start, i - start);
                    sb = new StringBuilder();
                    start = -1;
                }
                i += charLen;
                continue;
            }
            if (start < 0)
            {
                start = i;
            }
            sb.Append(CleanupChar(text[i]));
            if (charLen == 2)
            {
                sb.Append(CleanupChar(text[i + 1]));
            }
            if (IsSplitChar(c) || charLen == 2)
            {
                yield return (sb, start, i - start + charLen);
                sb = new StringBuilder();
                start = -1;
            }
            i += charLen;
        }
        if (sb.Length > 0)
        {
            yield return (sb, start, text.Length - start);
        }
    }

    private IEnumerable<Token> WordPiece(StringBuilder word, int start, int length)
    {
        var s = word.ToString();
        var pieces = new List<string>();
        var cursor = 0;
        var unknown = false;
        while (cursor < s.Length)
        {
            var end = s.Length;
            string? candidate = null;
            while (end > cursor)
            {
                var piece = cursor == 0 ? s[cursor..end] : "##" + s[cursor..end];
                if (_vocab.ContainsKey(piece))
                {
                    candidate = piece;
                    break;
                }
                end--;
            }
            if (candidate is null)
            {
                pieces.Clear();
                pieces.Add(UnkToken);   // BERT 标准：整词降级 UNK
                unknown = true;
                break;
            }
            pieces.Add(candidate);
            cursor = end;
        }
        var id = unknown ? UnkId : -1;
        foreach (var piece in pieces)
        {
            yield return new Token(unknown ? id : _vocab[piece], piece, start, length);
        }
    }

    private static char CleanupChar(char c)
    {
        if (c == 0x3000)
        {
            return ' ';   // 全角空格
        }
        if ((int)c is >= 0xFF01 and <= 0xFF5E)
        {
            c = (char)(c - 0xFEE0);   // 全角 → 半角
        }
        return char.ToLowerInvariant(c);
    }

    private static bool IsCjk(char c) =>
        (int)c is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0xF900 and <= 0xFAFF;

    private static bool IsPunctuation(char c) => char.IsPunctuation(c);

    /// <summary>单字切分：CJK / 标点 / 控制符 / UTF-16 代理对（emoji 等）/ 符号——
    /// 防止未知字符吞进相邻词导致后续文本一起降级 [UNK]（17 号风险 2 变体）。</summary>
    private static bool IsSplitChar(char c) =>
        IsCjk(c) || IsPunctuation(c) || char.IsControl(c) || char.IsSurrogate(c) || char.IsSymbol(c);
}
