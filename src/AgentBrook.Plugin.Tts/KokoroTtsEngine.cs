using SherpaOnnx;

namespace AgentBrook.Plugin.Tts;

/// <summary>
/// Kokoro (sherpa-onnx OfflineTts) 合成引擎封装——合成/播放分离（doc/Voice/07 §2.2）。
/// 纪律同 SenseVoiceEngine：懒加载（首次合成才建 ONNX session）、SemaphoreSlim(1,1) 串行
/// （ONNX session 非并发安全兜底）、停用/卸载经 Dispose 释放（doc/19 红线 2：零残留）。
/// 关键配置（S0 实证）：v1.1 英文 G2P 走 misaki（sherpa-onnx v2.0 起已移除 espeak-ng 依赖，
/// DataDir 留空；GPL 灰区的 espeak-ng-data 不随模型分发）；中英 lexicon 双文件逗号分隔；
/// RuleFsts 挂 phone/date/number 归一化（数字、日期读法正确）。
/// </summary>
public sealed class KokoroTtsEngine : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string> _log;
    private OfflineTts? _tts;
    private string _modelDir = "";
    private bool _disposed;

    public KokoroTtsEngine(Action<string> log) => _log = log;

    /// <summary>引擎是否已加载内存（遥测/日志用）。</summary>
    public bool IsLoaded => _tts is not null;

    /// <summary>当前输出采样率（未加载为 0）。</summary>
    public int SampleRate => _tts?.SampleRate ?? 0;

    /// <summary>绑定模型目录（不加载；首次合成时懒加载）。目录内缺关键文件返回 false。</summary>
    public bool Bind(string modelDir)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(Path.Combine(modelDir, "model.int8.onnx"))) return false;
        if (_modelDir == modelDir && _tts is not null) return true;
        if (_modelDir != modelDir)
        {
            Reset();
        }
        _modelDir = modelDir;
        return true;
    }

    /// <summary>释放已加载引擎（换模型组/停用时调用；下次合成重新懒加载）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _tts?.Dispose();
            _tts = null;
        }
    }

    /// <summary>
    /// 同步合成（调用方负责 Task.Run + 超时）。sid=音色在 voices.bin 中的序号。
    /// 返回 (采样率, float[-1..1] 样本)；失败抛异常由调用方归类错误码。
    /// </summary>
    public (int SampleRate, float[] Samples) Generate(string text, float speed, int sid, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        _gate.Wait(ct);
        try
        {
            if (_tts is null)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _tts = new OfflineTts(BuildConfig(_modelDir));
                _log($"[tts] 引擎懒加载完成（{sw.ElapsedMilliseconds}ms, {_tts.SampleRate}Hz）");
            }
            var audio = _tts.Generate(text, speed, sid);
            return (audio.SampleRate, audio.Samples);
        }
        finally
        {
            _gate.Release();
        }
    }

    private OfflineTtsConfig BuildConfig(string dir)
    {
        var kokoro = new OfflineTtsKokoroModelConfig
        {
            Model = Path.Combine(dir, "model.int8.onnx"),
            Voices = Path.Combine(dir, "voices.bin"),
            Tokens = Path.Combine(dir, "tokens.txt"),
            Lexicon = string.Join(",",
                Path.Combine(dir, "lexicon-zh.txt"),
                Path.Combine(dir, "lexicon-us-en.txt"),
                Path.Combine(dir, "lexicon-gb-en.txt")),
            DictDir = Path.Combine(dir, "dict"),
            DataDir = "",   // sherpa-onnx v2.0 起 espeak-ng 已移除；espeak-ng-data 不随模型分发（R2 定案）
        };
        var cfg = new OfflineTtsConfig
        {
            RuleFsts = string.Join(",",
                Path.Combine(dir, "phone-zh.fst"),
                Path.Combine(dir, "date-zh.fst"),
                Path.Combine(dir, "number-zh.fst")),
            MaxNumSentences = 1,
        };
        cfg.Model.Kokoro = kokoro;
        cfg.Model.NumThreads = 2;
        cfg.Model.Debug = 0;
        return cfg;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _tts?.Dispose();
            _tts = null;
        }
        _gate.Dispose();
    }
}
