using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AgentBrook.Plugin.Abstractions;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AgentBrook.Plugin.Asr.Tests")]

namespace AgentBrook.Plugin.Asr;

/// <summary>
/// 本地语音识别插件（doc/Voice/02）：sherpa-onnx + SenseVoice-Small int8 离线转写。
/// 以 IAsrCapability 能力供宿主发现（GetCapability），设置经 ISettingsContributor 声明式贡献
/// （title 用 host.Language 选择的三语字面量——宿主 I18n.T 未命中键原样返回）。
/// 模型落 workspace/models/asr/ 共享区（插件升级不重下）；下载一律走宿主统一下载服务。
/// 生命周期合规（doc/19）：ShutdownAsync 幂等释放引擎，无后台常驻线程。
/// </summary>
[PluginEntry("asr")]
public sealed class AsrPlugin : IHelperPlugin, ISettingsContributor, IAsrCapability
{
    private static readonly string DefaultEngine = ModelStore.SenseVoiceSmallInt8.Id;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private IPluginHostContext? _host;
    private ModelStore? _store;
    private SenseVoiceEngine? _engine;
    private string _engineId = DefaultEngine;
    private bool _initialized;

    public string Id => "asr";
    public string DisplayName => "本地语音识别";
    public string Version => "1.0.0";
    public string Icon => "🎤";

    public async Task InitializeAsync(IPluginHostContext host, CancellationToken cancellationToken = default)
    {
        _host = host;
        _store = new ModelStore(host.WorkspaceRoot, host.Download, host.Log);
        _engine = new SenseVoiceEngine(host.Log);

        // 设置真源 = 宿主 state.json（IPluginSettingsStore）；缺省补默认值
        var settings = ReadSettings();
        if (!settings.ContainsKey("engine"))
        {
            settings["engine"] = JsonSerializer.SerializeToElement(DefaultEngine);
            host.Settings.SetSettingsJson(JsonSerializer.Serialize(settings, JsonOpts));
        }
        _engineId = settings.TryGetValue("engine", out var e) ? e.ToString() ?? DefaultEngine : DefaultEngine;

        if (_store.GetInstalledPaths() is { } paths)
        {
            _engine.Bind(paths.ModelPath, paths.TokensPath);
            host.Log($"[asr] 模型已就绪：{paths.ModelPath}");
        }
        else
        {
            host.Log("[asr] 模型未安装，可在设置中下载（SenseVoice-Small ~228MB）");
        }

        _initialized = true;
        host.Log("本地语音识别插件已初始化");
    }

    public async Task ShutdownAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
            _engine = null;
        }
        _store = null;
        _initialized = false;
    }

    // ── IAsrCapability ─────────────────────────────────────────────────────

    public bool IsReady =>
        _initialized && _store?.IsInstalledQuick() == true && _engine is not null;

    public string? DescribeEngine() =>
        IsReady ? $"SenseVoice-Small (int8, 本地/{L("本地", "local", "lokal")})" : null;

    public async Task<AsrCapabilityResult> TranscribeAsync(AsrCapabilityRequest request, CancellationToken cancellationToken = default)
    {
        if (_engine is null || _store is null)
            return Fail("plugin-not-initialized");
        if (!_store.IsInstalledQuick())
            return Fail("model-not-installed");

        var paths = _store.GetInstalledPaths();
        if (paths is null) return Fail("model-not-installed");
        _engine.Bind(paths.Value.ModelPath, paths.Value.TokensPath);

        try
        {
            var (ok, text, error) = await _engine.TranscribeAsync(request.Pcm16kMono, request.SampleRate, request.LanguageHint, cancellationToken);
            if (!ok) return Fail(error ?? "transcribe-failed");
            return new AsrCapabilityResult
            {
                Success = true,
                Text = text,
                AudioDuration = TimeSpan.FromSeconds(request.Pcm16kMono.Length / 2.0 / Math.Max(1, request.SampleRate)),
            };
        }
        catch (OperationCanceledException)
        {
            return Fail("cancelled");
        }
        catch (Exception ex)
        {
            _host?.Log($"[asr] 转写异常：{ex.Message}");
            return Fail("engine-error");
        }

        AsrCapabilityResult Fail(string error) => new() { Success = false, Text = "", Error = error };
    }

    // ── ISettingsContributor ───────────────────────────────────────────────

    public string SettingsSchemaJson() => JsonSerializer.Serialize(new
    {
        groups = new object[]
        {
            new
            {
                title = L("引擎", "Engine", "Mesin"),
                items = (object[])
                [
                    new { key = "asr.engine", type = "choice", title = L("转写引擎", "Transcription engine", "Mesin transkripsi"),
                          choices = new[] { ModelStore.SenseVoiceSmallInt8.Id } },
                    new { key = "asr.model", type = "model",
                          title = L("转写模型", "Transcription model", "Model transkripsi"),
                          name = "SenseVoice-Small (int8, ~228MB)",
                          installAction = "install-model",
                          checkAction = "run-self-check",
                          readyAction = "model-ready" },
                    new { key = "asr.test", type = "action", action = "test-sample",
                          title = L("用内置样例测试转写", "Test with built-in sample", "Uji dengan sampel bawaan") },
                    new { key = "asr.remove", type = "action", action = "remove-model",
                          title = L("删除已下载模型", "Remove downloaded model", "Hapus model terunduh") },
                ],
            },
        },
    });

    public string GetSettingsJson() => JsonSerializer.Serialize(ReadSettings(), JsonOpts);

    public void SetSettingsJson(string json)
    {
        if (_host is null) return;
        var merged = ReadSettings();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateObject())
                merged[p.Name] = JsonSerializer.SerializeToElement(p.Value);
        }
        catch (Exception ex)
        {
            _host.Log($"[asr] 设置解析失败：{ex.Message}");
            return;
        }
        _host.Settings.SetSettingsJson(JsonSerializer.Serialize(merged, JsonOpts));

        if (merged.TryGetValue("engine", out var e))
        {
            _engineId = e.ToString() ?? DefaultEngine;
            _engine?.Reset();   // 换引擎组 → 下次转写重新懒加载
        }
    }

    public IReadOnlyList<string> Actions { get; } =
    [
        "install-model", "model-ready", "run-self-check", "test-sample", "remove-model",
    ];

    public async Task<PluginActionResult> RunActionAsync(string action, string? argumentsJson = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_store is null || _engine is null) return new PluginActionResult(false, "plugin-not-initialized");
        switch (action)
        {
            case "install-model":
            {
                _engine.Reset();
                var (ok, msg) = await _store.InstallAsync(ModelStore.SenseVoiceSmallInt8, progress, cancellationToken);
                if (ok && _store.GetInstalledPaths() is { } paths) _engine.Bind(paths.ModelPath, paths.TokensPath);
                return new PluginActionResult(ok, ok ? L("模型安装完成", "Model installed", "Model terpasang") : msg);
            }
            case "model-ready":
            {
                var ready = _store.IsInstalledQuick();
                return new PluginActionResult(ready, ready ? "ready" : L("未安装", "Not installed", "Belum terpasang"));
            }
            case "run-self-check":
            {
                var (ok, msg) = await _store.VerifyAsync(ct: cancellationToken).ConfigureAwait(false);
                if (!ok) return new PluginActionResult(false, $"{L("校验失败", "Verify failed", "Verifikasi gagal")}: {msg}");
                progress?.Report(L("管线自检…", "Pipeline self-check…", "Pemeriksaan pipeline…"));
                var text = await SelfCheckAsync(cancellationToken);
                return new PluginActionResult(text is not null,
                    text is null ? L("引擎自检失败", "Engine self-check failed", "Pemeriksaan mesin gagal")
                                 : L($"引擎自检通过（样例输出：{Trim(text)}）", $"Engine OK (sample out: {Trim(text)})", $"Mesin OK (keluaran: {Trim(text)})"));
            }
            case "test-sample":
            {
                var text = await SelfCheckAsync(cancellationToken);
                return new PluginActionResult(text is not null,
                    text is null ? L("测试失败（引擎不可用）", "Test failed (engine unavailable)", "Uji gagal (mesin tidak tersedia)")
                                 : L($"样例转写：{Trim(text)}", $"Sample transcript: {Trim(text)}", $"Transkrip sampel: {Trim(text)}"));
            }
            case "remove-model":
            {
                _engine.Reset();
                var (ok, msg) = _store.Remove();
                return new PluginActionResult(ok, ok ? L("已删除（重装需重新下载）", "Removed (re-download needed)", "Dihapus (unduh ulang diperlukan)") : msg);
            }
            default:
                return new PluginActionResult(false, $"unknown-action:{action}");
        }
    }

    // ── 内部 ────────────────────────────────────────────────────────────────

    /// <summary>自检：内嵌真实中文语音样例（sample.wav 资源，纯内存解码）跑通全管线；异常返回 null。</summary>
    private async Task<string?> SelfCheckAsync(CancellationToken ct)
    {
        if (_engine is null || _store?.GetInstalledPaths() is not { } paths) return null;
        _engine.Bind(paths.ModelPath, paths.TokensPath);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            await using var res = asm.GetManifestResourceStream("AgentBrook.Plugin.Asr.sample.wav")
                ?? throw new InvalidOperationException("embedded sample.wav missing");
            using var ms = new MemoryStream();
            await res.CopyToAsync(ms, ct);
            var (sampleRate, samples) = Wav.ReadPcm16(ms.ToArray());
            var pcm = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
                BitConverter.GetBytes((short)Math.Clamp(samples[i] * 32767, short.MinValue, short.MaxValue)).CopyTo(pcm, i * 2);
            var (ok, text, _) = await _engine.TranscribeAsync(pcm, sampleRate, null, ct);
            return ok ? text : null;
        }
        catch (Exception ex)
        {
            _host?.Log($"[asr] 自检异常：{ex.Message}");
            return null;
        }
    }

    private Dictionary<string, JsonElement> ReadSettings()
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var raw = _host?.Settings.GetSettingsJson();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                using var doc = JsonDocument.Parse(raw);
                foreach (var p in doc.RootElement.EnumerateObject())
                    dict[p.Name] = JsonSerializer.SerializeToElement(p.Value);
            }
        }
        catch { /* 损坏设置回退默认 */ }
        return dict;
    }

    private string L(string zh, string en, string id) => (_host?.Language) switch
    {
        "en" => en,
        "id" => id,
        _ => zh,
    };

    private static string Trim(string text) => text.Length switch { 0 => "∅", > 24 => text[..24] + "…", _ => text };
}
