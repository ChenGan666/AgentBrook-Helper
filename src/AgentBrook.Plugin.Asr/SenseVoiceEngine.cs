using System.Text;
using System.Text.RegularExpressions;
using AgentBrook.Plugin.Abstractions;
using SherpaOnnx;

namespace AgentBrook.Plugin.Asr;

/// <summary>
/// SenseVoice 离线转写引擎（doc/Voice/02 A2）：sherpa-onnx OfflineRecognizer 封装。
/// 懒加载（首次转写才建 ONNX session，~600MB RAM 用时才占）；SemaphoreSlim(1,1) 串行
/// （ONNX session 非并发安全兜底）；停用/卸载经 DisposeAsync 释放（doc/19 合规：零残留）。
/// G-A0 spike 结论（2026-11-06）：输入 = AcceptWaveform(sampleRate, float[])（16bit PCM/32768）；
/// 2.0.1 native 层已剥语言/情绪标签，防御性剥离仅作版本兜底；ITN 固定开启（输出带标点+数字归一化）。
/// </summary>
public sealed class SenseVoiceEngine : IAsyncDisposable
{
    /// <summary>防御性剥除 SenseVoice 特殊标签（语言/情绪/事件），2.0.1 实测输出已无标签，防未来版本变化。</summary>
    internal static readonly Regex SpecialTagPattern = new(@"<\|[^|>]*\|>", RegexOptions.Compiled);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string> _log;
    private OfflineRecognizer? _recognizer;
    private string _modelPath = "";
    private string _tokensPath = "";
    private bool _disposed;

    public SenseVoiceEngine(Action<string> log) => _log = log;

    /// <summary>引擎是否已加载内存（遥测/测试用）。</summary>
    public bool IsLoaded => _recognizer is not null;

    /// <summary>
    /// 转写 16bit LE 单声道 PCM。引擎按需加载；串行执行；取消点在等待与解码前（解码本身秒级，不中断）。
    /// </summary>
    public async Task<(bool Ok, string Text, string? Error)> TranscribeAsync(
        ReadOnlyMemory<byte> pcm, int sampleRate, string? languageHint, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var recognizer = EnsureLoaded();
            if (recognizer is null)
                return (false, "", "model-not-installed");
            ct.ThrowIfCancellationRequested();

            var samples = PcmToFloat(pcm);
            if (samples.Length == 0)
                return (false, "", "empty-audio");

            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(sampleRate, samples);
            recognizer.Decode(stream);
            var text = StripSpecialTags(stream.Result.Text).Trim();
            return (true, text, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>确保识别器就绪；模型文件缺失返回 null（不抛）。</summary>
    private OfflineRecognizer? EnsureLoaded()
    {
        if (_recognizer is not null) return _recognizer;
        if (_modelPath.Length == 0 || !File.Exists(_modelPath) || !File.Exists(_tokensPath))
            return null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.ModelConfig.SenseVoice.Model = _modelPath;
        config.ModelConfig.SenseVoice.Language = "";                       // 自动语种（zh/en/ja/ko/yue）
        config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;     // 标点 + 数字归一化（G-A0 结论）
        config.ModelConfig.Tokens = _tokensPath;
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Debug = 0;
        _recognizer = new OfflineRecognizer(config);
        _log($"[asr] 引擎已加载（{sw.ElapsedMilliseconds}ms）：{_modelPath}");
        return _recognizer;
    }

    /// <summary>绑定模型文件（安装/自检后调用）；下次转写生效，已加载实例保持不动（换组时显式 Reset）。</summary>
    public void Bind(string modelPath, string tokensPath)
    {
        _modelPath = modelPath;
        _tokensPath = tokensPath;
        if (_recognizer is not null && (!File.Exists(modelPath) || modelPath != _modelPath)) Reset();
    }

    /// <summary>卸载已加载引擎（释放 native session；下次转写重新懒加载）。</summary>
    public void Reset()
    {
        _recognizer?.Dispose();
        _recognizer = null;
    }

    /// <summary>PCM 16bit LE → float（-1..1）。</summary>
    internal static float[] PcmToFloat(ReadOnlyMemory<byte> pcm)
    {
        var span = pcm.Span;
        var samples = new float[span.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(span[(i * 2)..]) / 32768f;
        return samples;
    }

    internal static string StripSpecialTags(string text) =>
        SpecialTagPattern.Replace(text ?? "", "").Trim();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _gate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
