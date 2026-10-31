using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AgentBrook.Privacy.Ner;

/// <summary>
/// ONNX Runtime CPU 实现的 token 分类 NER 模型（17 号 §3.1）。
/// 输入 input_ids + attention_mask（模型元数据要求 token_type_ids 时补全零单句输入）；
/// 输出取首个 logits 张量 [1, seq, labels]，逐位置 softmax 取 argmax。
/// </summary>
public sealed class OnnxNerModel : INerModel
{
    private readonly InferenceSession _session;
    private readonly IReadOnlyList<string> _labels;
    private readonly bool _needsTokenTypeIds;

    public OnnxNerModel(string modelPath, string labelsPath, int maxSequenceTokens = 510, int threads = 2)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
        };
        _session = new InferenceSession(modelPath, options);
        _labels = File.ReadAllLines(labelsPath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        _needsTokenTypeIds = _session.InputMetadata.Keys.Any(k => k == "token_type_ids");
        if (_labels.Count == 0)
        {
            throw new InvalidOperationException($"标签文件为空：{labelsPath}");
        }
        MaxSequenceTokens = maxSequenceTokens;
    }

    public int MaxSequenceTokens { get; }

    public IReadOnlyList<string> Labels => _labels;

    public NerTokenLabel[] Predict(IReadOnlyList<int> tokenIds)
    {
        var seq = tokenIds.Count;
        var inputIds = new DenseTensor<long>([1, seq]);
        var attentionMask = new DenseTensor<long>([1, seq]);
        for (var i = 0; i < seq; i++)
        {
            inputIds[0, i] = tokenIds[i];
            attentionMask[0, i] = 1;
        }
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
        };
        if (_needsTokenTypeIds)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>([1, seq])));
        }
        using var outputs = _session.Run(inputs);
        var logits = outputs[0].AsTensor<float>();   // [1, seq, labels]
        var nLabels = logits.Dimensions[2];
        var result = new NerTokenLabel[seq];
        for (var pos = 0; pos < seq; pos++)
        {
            var max = float.NegativeInfinity;
            var best = 0;
            for (var l = 0; l < nLabels; l++)
            {
                var v = logits[0, pos, l];
                if (v > max)
                {
                    max = v;
                    best = l;
                }
            }
            var sum = 0d;
            var bestExp = 0d;
            for (var l = 0; l < nLabels; l++)
            {
                var e = Math.Exp(logits[0, pos, l] - max);   // 减 max 数值稳定
                sum += e;
                if (l == best)
                {
                    bestExp = e;
                }
            }
            result[pos] = new NerTokenLabel(best, (float)(bestExp / sum));
        }
        return result;
    }

    public void Dispose() => _session.Dispose();
}
