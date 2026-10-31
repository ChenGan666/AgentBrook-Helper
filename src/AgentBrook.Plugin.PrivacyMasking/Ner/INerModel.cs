namespace AgentBrook.Privacy.Ner;

/// <summary>单 token 的推理结果：标签 id 与该标签的 softmax 概率。</summary>
public readonly record struct NerTokenLabel(int LabelId, float Probability);

/// <summary>
/// NER 模型抽象（token 分类 / BIO）。实现负责推理细节（<see cref="OnnxNerModel"/> = ONNX Runtime CPU），
/// 识别器只消费标签表与逐 token 概率——单测用 stub 即可覆盖解码 / 阈值 / 分块逻辑。
/// </summary>
public interface INerModel : IDisposable
{
    /// <summary>单次推理最大 token 数（不含 CLS / SEP）。</summary>
    int MaxSequenceTokens { get; }

    /// <summary>标签表（id → 标签名，如 O / B-PER / I-ORG）。</summary>
    IReadOnlyList<string> Labels { get; }

    /// <summary>对一段 token 序列推理；返回数组长度与 tokenIds 一致。</summary>
    NerTokenLabel[] Predict(IReadOnlyList<int> tokenIds);
}
