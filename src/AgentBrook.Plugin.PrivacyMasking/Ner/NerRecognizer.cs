using AgentBrook.Privacy.Detectors;

namespace AgentBrook.Privacy.Ner;

/// <summary>
/// 本地 ONNX NER 识别器（17 号方案，IRecognizer 同构）：词典 / 规则盲区（未登记人名 / 机构 / 地址）的兜底识别。
/// Priority 30（最低，同段命中词典 / 规则必胜）；**AutoFill=false——命中只做在途脱敏，不进词典**（防误报固化）；
/// 失败 fail-open（模型缺失 / 推理异常返回空集，规则层照常）；单条超长截断跳过（效率护栏）。
/// 分块：maxSeq-2 token 滑窗（overlap 64），逐块 BIO 解码 → 阈值过滤 → 跨块去重。
/// </summary>
public sealed class NerRecognizer : IRecognizer
{
    public const int NerPriority = 30;
    private const int ChunkOverlapTokens = 64;
    private const int DefaultMaxChars = 32_768;

    private readonly INerModel _model;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly IReadOnlyDictionary<string, bool> _categories;
    private readonly double _threshold;
    private readonly int _maxChars;
    private readonly int _window;

    public NerRecognizer(INerModel model, WordPieceTokenizer tokenizer,
        IReadOnlyDictionary<string, bool> categories, double threshold, int maxChars = DefaultMaxChars)
    {
        _model = model;
        _tokenizer = tokenizer;
        _categories = categories;
        _threshold = threshold;
        _maxChars = maxChars;
        _window = Math.Max(8, model.MaxSequenceTokens - 2);
    }

    /// <summary>按快照配置构建；未启用 / 模型文件缺失 / 加载失败 → null（fail-open，规则层照常）。</summary>
    /// <remarks>模型目录约定：modelsDir/&lt;NerModel&gt;/{model.onnx, vocab.txt, labels.txt}。</remarks>
    public static NerRecognizer? CreateIfConfigured(PrivacySnapshot snapshot, string modelsDir)
    {
        if (!snapshot.NerEnabled)
        {
            return null;
        }
        var dir = Path.Combine(modelsDir, snapshot.NerModel);
        var modelFile = Path.Combine(dir, "model.onnx");
        var vocabFile = Path.Combine(dir, "vocab.txt");
        var labelsFile = Path.Combine(dir, "labels.txt");
        if (!File.Exists(modelFile) || !File.Exists(vocabFile) || !File.Exists(labelsFile))
        {
            return null;
        }
        try
        {
            var tokenizer = new WordPieceTokenizer(vocabFile);
            var model = new OnnxNerModel(modelFile, labelsFile);
            return new NerRecognizer(model, tokenizer, snapshot.NerCategories, snapshot.NerThreshold);
        }
        catch
        {
            return null;   // 模型损坏 / 不兼容 → fail-open
        }
    }

    public IReadOnlyList<PrivacyMatch> Detect(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > _maxChars)
        {
            return [];   // 效率护栏：超长单条跳过 NER（规则层照扫）
        }
        try
        {
            var tokens = (List<WordPieceTokenizer.Token>)_tokenizer.Tokenize(text, int.MaxValue);
            if (tokens.Count == 0)
            {
                return [];
            }
            List<PrivacyMatch>? matches = null;
            var overlap = Math.Min(ChunkOverlapTokens, _window / 2);   // overlap 不得超过窗口一半（防滑窗退化）
            var chunkStart = 0;
            while (chunkStart < tokens.Count)
            {
                var chunkEnd = Math.Min(tokens.Count, chunkStart + _window);
                var isLast = chunkEnd >= tokens.Count;
                DecodeChunk(text, tokens.GetRange(chunkStart, chunkEnd - chunkStart), matches ??= [], isLast);
                if (isLast)
                {
                    break;
                }
                chunkStart = Math.Max(chunkEnd - overlap, chunkStart + 1);
            }
            // 跨块去重（重叠区同一实体会被重复解码）
            return (matches ?? [])
                .GroupBy(m => (m.Start, m.Length, m.Category))
                .Select(g => g.First())
                .OrderBy(m => m.Start)
                .ToList();
        }
        catch
        {
            return [];   // fail-open：模型 / 分词异常不阻塞规则层
        }
    }

    private void DecodeChunk(string text, List<WordPieceTokenizer.Token> chunkTokens,
        List<PrivacyMatch> output, bool isLastChunk)
    {
        var ids = new List<int> { _tokenizer.ClsId };
        ids.AddRange(chunkTokens.Select(t => t.Id));
        ids.Add(_tokenizer.SepId);
        var labels = _model.Predict(ids);

        string? openType = null;
        var openStart = -1;
        var openEnd = -1;
        var probSum = 0d;
        var probCount = 0;

        void AddEntity(string type, int start, int end, double sum, int count)
        {
            var category = MapCategory(type);
            if (category is not null && (sum / Math.Max(1, count)) >= _threshold && IsCategoryEnabled(category))
            {
                output.Add(new PrivacyMatch(start, end - start, category,
                    text[start..end], NerPriority, AutoFill: false));
            }
        }

        // BIOES（P0a: ckiplab 系列 = OntoNotes 18 类）：S- 单字 / B- 开 / I- 延续 / E- 收 / O
        for (var i = 0; i < chunkTokens.Count; i++)
        {
            var label = _model.Labels[labels[i + 1].LabelId];   // +1 跳过 CLS 位
            var prob = (double)labels[i + 1].Probability;
            var prefix = label.Length > 2 && label[1] == '-' ? label[..2] : null;
            var type = prefix is null ? null : label[2..];
            var sameOpen = type is not null && string.Equals(type, openType, StringComparison.Ordinal);
            var tokenStart = chunkTokens[i].Start;
            var tokenEnd = tokenStart + chunkTokens[i].Length;

            switch (prefix)
            {
                case "S-":
                    if (openType is not null)
                    {
                        AddEntity(openType, openStart, openEnd, probSum, probCount);
                    }
                    AddEntity(type!, tokenStart, tokenEnd, prob, 1);
                    openType = null;
                    break;
                case "B-":
                    if (openType is not null)
                    {
                        AddEntity(openType, openStart, openEnd, probSum, probCount);
                    }
                    openType = type;
                    openStart = tokenStart;
                    openEnd = tokenEnd;
                    probSum = prob;
                    probCount = 1;
                    break;
                case "E-":
                    if (sameOpen)
                    {
                        AddEntity(openType!, openStart, tokenEnd, probSum + prob, probCount + 1);
                    }
                    else
                    {
                        if (openType is not null)
                        {
                            AddEntity(openType, openStart, openEnd, probSum, probCount);
                        }
                        AddEntity(type!, tokenStart, tokenEnd, prob, 1);
                    }
                    openType = null;
                    break;
                case "I-":
                    if (sameOpen)
                    {
                        openEnd = tokenEnd;
                        probSum += prob;
                        probCount++;
                    }
                    else
                    {
                        if (openType is not null)
                        {
                            AddEntity(openType, openStart, openEnd, probSum, probCount);
                        }
                        openType = type;
                        openStart = tokenStart;
                        openEnd = tokenEnd;
                        probSum = prob;
                        probCount = 1;
                    }
                    break;
                default:
                    if (openType is not null)
                    {
                        AddEntity(openType, openStart, openEnd, probSum, probCount);
                    }
                    openType = null;
                    break;
            }
        }
        // 块尾仍开启的实体：中间块丢弃（滑窗 overlap 保证下一块完整捕获，避免窗口截断产生碎片实体）；
        // 最后一块没有下一块 → 收口输出
        if (isLastChunk && openType is not null)
        {
            AddEntity(openType, openStart, openEnd, probSum, probCount);
        }
    }

    /// <summary>NER 标签类型 → 占位符类别。支持两套标签集：OntoNotes 18 类（ckiplab 系列，P0a 默认档）
    /// 与 CLUENER2020 10 类（uer 基线 / 微调路径，17 号 §3.2）；DATE/MONEY/BOOK 等范围外不映射。</summary>
    private static string? MapCategory(string nerType) => nerType switch
    {
        "PER" or "PERSON" or "name" => PrivacyCategories.Person,
        "ORG" or "company" or "organization" or "government" => PrivacyCategories.Org,
        "LOC" or "GPE" or "FAC" or "ADDR" or "ADDRESS" or "address" or "scene" => PrivacyCategories.Address,
        _ => null,
    };

    private bool IsCategoryEnabled(string category) =>
        _categories.TryGetValue(category, out var on) ? on : true;
}
