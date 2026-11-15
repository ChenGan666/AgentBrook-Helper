using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentBrook.Plugin.Abstractions;
using AgentBrook.Plugin.Tts.Audio;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AgentBrook.Plugin.Tts.Tests")]

namespace AgentBrook.Plugin.Tts;

/// <summary>
/// 本地语音播报插件（doc/Voice/07）：sherpa-onnx + Kokoro v1.1 中英 int8 离线合成 + 本地播放。
/// 以 ITtsCapability 能力供宿主发现（GetCapability——回合完成自动播报链）；
/// 设置经 ISettingsContributor 声明式贡献（title 用 host.Language 三语字面量）。
/// 模型落 workspace/models/tts/ 共享区（插件升级/重装不重下）；下载一律走宿主统一下载服务。
/// 播放路径 = 临时 wav 短暂物化（workspace/voice/tmp/，播完即删；A2 定案）。
/// 生命周期合规（doc/19）：ShutdownAsync 幂等释放引擎与播放器，无后台常驻线程。
/// </summary>
[PluginEntry("tts")]
public sealed class TtsPlugin : IHelperPlugin, ISettingsContributor, ITtsCapability
{
    // ── 出厂音色表（1.0.5：用户全量试听定稿三选——zf_024/zm_010/af_maple；
    //    sid=voices.bin 序号，完整 103 音色映射见 doc/Voice/07 附录 B）──
    internal static readonly (string Id, int Sid, string Zh, string En, string IdLang)[] Voices =
    [
        ("zf_024", 17, "晓语（女·自然）", "Xiaoyu (F, natural)", "Xiaoyu (P, alami)"),
        ("zm_010", 59, "慕辰（男·自然）", "Muchen (M, natural)", "Muchen (L, alami)"),
        ("af_maple", 0, "Maple（英文音色）", "Maple (English voice)", "Maple (suara Inggris)"),
    ];

    /// <summary>风格预设（诚实映射——引擎无风格控制，风格=音色×语速组合，doc/Voice/07 §4.2）。
    /// 1.0.5：女声只剩 zf_024 一档，活泼=同音色提速（语速驱动）。</summary>
    internal static readonly (string Id, string Voice, float Speed, string Zh, string En, string IdLang)[] Presets =
    [
        ("natural", "zf_024", 1.0f, "自然（女声·标准语速）", "Natural (female, standard)", "Alami (P, standar)"),
        ("steady", "zm_010", 0.9f, "沉稳（男声·略慢）", "Steady (male, slower)", "Tenang (L, lebih lambat)"),
        ("lively", "zf_024", 1.15f, "活泼（女声·略快）", "Lively (female, faster)", "Ceria (P, lebih cepat)"),
        ("custom", "", 1.0f, "自定义（手动选音色/语速）", "Custom (manual voice/speed)", "Kustom (manual)"),
    ];

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private const string DefaultModelGroupId = "kokoro-int8-multi-lang-v1_1";

    /// <summary>
    /// Windows 原生隔离（doc/Voice/07 §5.7，配方同 ASR）：PrivacyMasking/ASR 均已占用自己的
    /// onnxruntime 名字，本插件包内改名 ort_tts.dll + 补丁 c-api 导入名（构建脚本），
    /// 此处全路径预载抢占 + 解析器把 sherpa 管理层指向插件目录。
    /// </summary>
    private static void PreloadWindowsNatives(IPluginHostContext host)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var nativeDir = Path.GetDirectoryName(typeof(KokoroTtsEngine).Assembly.Location) ?? ".";
            var ort = Path.Combine(nativeDir, "ort_tts.dll");
            if (!File.Exists(ort)) ort = Path.Combine(nativeDir, "onnxruntime.dll");   // 旧包（未改名）兼容
            if (File.Exists(ort))
            {
                NativeLibrary.Load(ort);
                host.Log($"[tts] Windows 原生隔离已预载：{Path.GetFileName(ort)}");
            }
            NativeLibrary.SetDllImportResolver(typeof(KokoroTtsEngine).Assembly, (libraryName, assembly, searchPath) =>
            {
                var p = Path.Combine(nativeDir, libraryName);
                return File.Exists(p) ? NativeLibrary.Load(p, assembly, searchPath) : IntPtr.Zero;
            });
        }
        catch (Exception ex)
        {
            host.Log($"[tts] Windows 原生预载失败（播报可能不可用）：{ex.Message}");
        }
    }

    private IPluginHostContext? _host;
    private TtsModelStore? _store;
    private KokoroTtsEngine? _engine;
    private IAudioPlayer? _player;
    private bool _initialized;

    /// <summary>播报单槽（一次一路；新请求 InterruptCurrent → 取消旧的再排队）。</summary>
    private readonly SemaphoreSlim _speakGate = new(1, 1);
    private CancellationTokenSource? _currentSpeak;

    private string TmpDir => Path.Combine(_host?.WorkspaceRoot ?? ".", "voice", "tmp");

    public string Id => "tts";
    public string DisplayName => "本地语音播报";
    public string Version => "1.0.6";
    public string Icon => "🔊";

    public Task InitializeAsync(IPluginHostContext host, CancellationToken cancellationToken = default)
    {
        _host = host;
        TtsLog.Bind(host.WorkspaceRoot);
        _store = new TtsModelStore(host.WorkspaceRoot, host.Download, host.Log);
        _engine = new KokoroTtsEngine(msg => { host.Log(msg); TtsLog.Write(msg); });
        _player = CreatePlayer();
        PreloadWindowsNatives(host);

        // 设置缺省补默认值（宿主 slider 默认显示 (min+max)/2——必须显式落盘默认，doc/07 §5.6）
        var settings = ReadSettings();
        var defaults = new Dictionary<string, object?>
        {
            ["tts.auto_announce"] = true,
            ["tts.announce_timing"] = "background",
            ["tts.announce_language"] = "follow",
            ["tts.style"] = "natural",
            ["tts.voice"] = "zf_024",
            ["tts.speed"] = 1.0,
            ["tts.volume"] = 80.0,
        };
        var changed = false;
        foreach (var (k, v) in defaults)
        {
            if (!settings.ContainsKey(k))
            {
                settings[k] = JsonSerializer.SerializeToElement(v);
                changed = true;
            }
        }
        if (changed)
        {
            host.Settings.SetSettingsJson(JsonSerializer.Serialize(settings, JsonOpts));
        }

        // 临时目录清扫（启动清残留；A2：短暂物化，播完即删）
        try
        {
            if (Directory.Exists(TmpDir))
            {
                foreach (var f in Directory.EnumerateFiles(TmpDir, "*.wav"))
                {
                    try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1)) File.Delete(f); }
                    catch { }
                }
            }
        }
        catch { }

        if (_store.IsInstalledQuick())
        {
            _engine.Bind(_store.GroupDir());
            host.Log($"[tts] 模型已就绪：{_store.GroupDir()}");
        }
        else
        {
            host.Log("[tts] 模型未安装，可在设置中下载（Kokoro v1.1 中英 int8 ~170MB）");
        }

        _initialized = true;
        host.Log("本地语音播报插件已初始化");
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync()
    {
        _currentSpeak?.Cancel();
        _player?.Stop();
        _player?.Dispose();
        _player = null;
        if (_engine is not null)
        {
            _engine.Dispose();
            _engine = null;
        }
        _store = null;
        _initialized = false;
        await Task.CompletedTask;
    }

    private static IAudioPlayer CreatePlayer() => OperatingSystem.IsWindows()
        ? new WindowsAudioPlayer()
        : new MacAudioPlayer();

    // ── ITtsCapability ─────────────────────────────────────────────────────

    public bool IsReady => _initialized && _store?.IsInstalledQuick() == true && _engine is not null;

    public bool AutoAnnounceEnabled => ReadBool("tts.auto_announce", true);

    /// <summary>播报时机归一化：设置存的是三语 choice 字面量，读取时映射回稳定 id。</summary>
    internal static string NormalizeTiming(string? raw) => raw switch
    {
        "always" or "总是播报" or "Always" or "Selalu" => "always",
        _ => "background",   // background/仅窗口后台时/Only when window inactive/Hanya saat jendela tidak aktif/缺省
    };

    internal static string NormalizeLanguage(string? raw) => raw switch
    {
        "zh" or "中文" or "Chinese" or "Mandarin" => "zh",
        "en" or "English" or "Inggris" => "en",
        _ => "follow",   // follow/跟随界面语言/Follow UI language/Ikuti bahasa UI/缺省
    };

    public bool AnnounceOnlyWhenInactive =>
        NormalizeTiming(ReadString("tts.announce_timing")) != "always";

    public TtsAnnounceLanguage AnnounceLanguage => NormalizeLanguage(ReadString("tts.announce_language")) switch
    {
        "zh" => TtsAnnounceLanguage.Chinese,
        "en" => TtsAnnounceLanguage.English,
        _ => TtsAnnounceLanguage.FollowUi,
    };

    public TtsEngineStatus DescribeEngine()
    {
        if (_store is null || _engine is null) return NotReady("tts.not-initialized");
        if (!_store.IsInstalledQuick()) return NotReady("tts.model.missing");
        var lang = _host?.Language ?? "zh";
        return new TtsEngineStatus
        {
            Ready = true,
            EngineName = $"Kokoro v1.1 中英 (int8, {L("本地", "local", "lokal")})",
            Voices = Voices.Select(v => new TtsVoiceInfo
            {
                Id = v.Id,
                Name = lang == "en" ? v.En : lang == "id" ? v.IdLang : v.Zh,
                Preset = Presets.FirstOrDefault(p => p.Voice == v.Id).Id,
            }).ToArray(),
        };

        TtsEngineStatus NotReady(string code) => new() { Ready = false, ErrorCode = code };
    }

    public async Task<TtsSpeakResult> SpeakAsync(TtsSpeakRequest request, CancellationToken cancellationToken = default)
    {
        if (_engine is null || _store is null || _player is null)
            return Fail("tts.not-initialized", "插件未初始化");
        if (string.IsNullOrWhiteSpace(request.Text))
            return Fail("tts.empty-text", "播报文本为空");
        if (!_store.IsInstalledQuick())
            return Fail("tts.model.missing", "模型未安装");

        // 单槽：打断当前（InterruptCurrent）或等待旧播报结束
        var ownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.InterruptCurrent)
        {
            _currentSpeak?.Cancel();
        }
        var entered = false;
        try
        {
            await _speakGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            _currentSpeak = ownCts;
            var ct = ownCts.Token;
            ct.ThrowIfCancellationRequested();

            var (voice, speed, volume) = ResolveVoiceSpeedVolume(request);
            var sid = Voices.FirstOrDefault(v => v.Id == voice).Sid;
            if (sid == 0 && voice != "af_maple")
            {
                // 存量设置的音色 id 不在新出厂表（换表升级）→ 回退默认音色，播报不失败
                voice = Voices[0].Id;
                sid = Voices[0].Sid;
                TtsLog.Write($"voice fallback: stored voice not in table → {voice}");
            }

            var sw = Stopwatch.StartNew();
            var modelDir = _store.GroupDir();
            if (!_engine.Bind(modelDir))
                return Fail("tts.model.missing", "模型目录不完整");

            // 句级流水线（doc/Voice/07 §8 优化）：第 n 段播放期间预合成第 n+1 段——
            // 首声时延从「全文合成」降到「首段合成」（60 字摘要实测 5-7s → 2-3s）；
            // 引擎经 SemaphoreSlim 串行，播放/合成重叠不产生并发推理。
            var parts = SplitSentences(request.Text);
            var totalAudioMs = 0;
            var firstVoiceMs = -1;
            var lastWavPath = "";
            try
            {
                (int Rate, float[] Samples)? pending = await Task.Run(() => _engine.Generate(parts[0], speed, sid, ct), ct).ConfigureAwait(false);
                for (int i = 0; i < parts.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(TmpDir);
                    var wavPath = Path.Combine(TmpDir, $"tts-{Guid.NewGuid():N}-{i}.wav");
                    Wav.WritePcm16(wavPath, pending!.Value.Samples, pending!.Value.Rate);
                    totalAudioMs += (int)(pending.Value.Samples.Length * 1000.0 / Math.Max(1, pending.Value.Rate));
                    if (firstVoiceMs < 0)
                    {
                        firstVoiceMs = (int)sw.ElapsedMilliseconds;   // 首声合成完成（流水线收益指标）
                    }
                    lastWavPath = wavPath;

                    var playTask = _player.PlayAsync(wavPath, volume, ct);
                    var next = i + 1 < parts.Count
                        ? await Task.Run(() => _engine.Generate(parts[i + 1], speed, sid, ct), ct).ConfigureAwait(false)
                        : default((int Rate, float[] Samples)?);
                    var (completed, playError) = await playTask.ConfigureAwait(false);
                    try { File.Delete(wavPath); } catch { }
                    if (!completed && playError is not ("tts.cancelled" or "tts.disposed"))
                    {
                        TtsLog.Write($"play fail: {playError}");
                        return Fail(playError ?? "tts.play.device", "播放失败");
                    }
                    if (!completed)
                    {
                        TtsLog.Write("play cancelled");
                        return new TtsSpeakResult { Success = false, ErrorCode = "tts.cancelled", AudioMs = totalAudioMs, SynthMs = (int)sw.ElapsedMilliseconds };
                    }
                    pending = next;
                }

                TtsLog.Write($"speak ok: chars={request.Text.Length} parts={parts.Count} voice={voice} speed={speed:F2} " +
                             $"firstVoiceMs={firstVoiceMs} totalMs={(int)sw.ElapsedMilliseconds} audioMs={totalAudioMs}");
                return new TtsSpeakResult
                {
                    Success = true,
                    WavPath = lastWavPath,
                    AudioMs = totalAudioMs,
                    SynthMs = (int)sw.ElapsedMilliseconds,
                };
            }
            finally
            {
                // 各段 wav 已在循环内播完即删；此处兜底清播放中断时的残留
                foreach (var f in Directory.EnumerateFiles(TmpDir, "tts-*.wav"))
                {
                    try { if (File.GetLastWriteTimeUtc(f) > DateTime.UtcNow.AddMinutes(-5)) File.Delete(f); } catch { }
                }
            }
        }
        catch (OperationCanceledException)
        {
            TtsLog.Write("speak cancelled");
            return new TtsSpeakResult { Success = false, ErrorCode = "tts.cancelled" };
        }
        catch (Exception ex)
        {
            _host?.Log($"[tts] 播报异常：{ex.Message}");
            TtsLog.Write($"speak error: {ex.GetType().Name}");
            return Fail("tts.synth", "合成失败");
        }
        finally
        {
            if (entered)
            {
                if (_currentSpeak == ownCts) _currentSpeak = null;
                ownCts.Dispose();
                _speakGate.Release();
            }
            else
            {
                ownCts.Dispose();
            }
        }

        TtsSpeakResult Fail(string code, string zhText) => new()
        {
            Success = false,
            ErrorCode = code,
            ErrorText = code == "tts.cancelled" ? zhText : L(zhText, "Speech failed", "Pengumuman gagal"),
        };
    }

    public async Task StopAsync()
    {
        try { _currentSpeak?.Cancel(); } catch { }
        try { _player?.Stop(); } catch { }
        await Task.CompletedTask;
    }

    // ── ISettingsContributor ───────────────────────────────────────────────

    public string SettingsSchemaJson() => JsonSerializer.Serialize(new
    {
        groups = new object[]
        {
            new
            {
                title = L("播报", "Announcement", "Pengumuman"),
                items = (object[])
                [
                    new { key = "tts.auto_announce", type = "toggle",
                          title = L("任务完成后自动播报", "Auto announce after task completes", "Otomatis umumkan setelah tugas selesai") },
                    new { key = "tts.announce_timing", type = "choice",
                          title = L("播报时机", "Announce timing", "Waktu pengumuman"),
                          choices = new[] { L("仅窗口后台时", "Only when window inactive", "Hanya saat jendela tidak aktif"),
                                            L("总是播报", "Always", "Selalu") } },
                    new { key = "tts.announce_language", type = "choice",
                          title = L("播报语言", "Announce language", "Bahasa"),
                          choices = new[] { L("跟随界面语言", "Follow UI language", "Ikuti bahasa UI"),
                                            L("中文", "Chinese", "Mandarin"),
                                            L("English", "English", "Inggris") } },
                ],
            },
            new
            {
                title = L("语音", "Voice", "Suara"),
                items = (object[])
                [
                    new { key = "tts.style", type = "choice",
                          title = L("语音风格", "Voice style", "Gaya suara"),
                          choices = Presets.Select(p => p.Id switch
                          {
                              "natural" => L("自然", "Natural", "Alami"),
                              "steady" => L("沉稳", "Steady", "Tenang"),
                              "lively" => L("活泼", "Lively", "Ceria"),
                              _ => L("自定义", "Custom", "Kustom"),
                          }).ToArray() },
                    new { key = "tts.voice", type = "choice",
                          title = L("音色", "Voice", "Suara"),
                          choices = Voices.Select(v => v.Id).ToArray() },
                    new { key = "tts.speed", type = "slider",
                          title = L("语速", "Speed", "Kecepatan"),
                          min = 0.7, max = 1.5, step = 0.05 },
                    new { key = "tts.volume", type = "slider",
                          title = L("音量", "Volume", "Volume"),
                          min = 0.0, max = 100.0, step = 5.0 },
                    new { key = "tts.style-note", type = "note",
                          title = L("风格为音色×语速的预设组合（引擎不支持独立风格控制）；风格≠自定义时音色与语速跟随预设。",
                                    "Style is a preset of voice × speed (the engine has no independent style control); when style ≠ Custom, voice and speed follow the preset.",
                                    "Gaya adalah preset suara × kecepatan (mesin tidak punya kontrol gaya independen); saat gaya ≠ Kustom, suara & kecepatan mengikuti preset.") },
                ],
            },
            new
            {
                title = L("引擎", "Engine", "Mesin"),
                items = (object[])
                [
                    new { key = "tts.model", type = "model",
                          title = L("合成模型", "Synthesis model", "Model sintesis"),
                          name = "Kokoro v1.1 中英 (int8, ~170MB)",
                          installAction = "install-model",
                          checkAction = "run-self-check",
                          readyAction = "model-ready" },
                    new { key = "tts.test", type = "action", action = "test-voice",
                          title = L("试听当前语音", "Preview current voice", "Pratinjau suara saat ini") },
                    new { key = "tts.remove", type = "action", action = "remove-model",
                          title = L("删除已下载模型", "Remove downloaded model", "Hapus model terunduh") },
                    new { key = "tts.engine-status", type = "note",
                          title = EngineStatusText() },
                ],
            },
        },
    });

    public string GetSettingsJson() => JsonSerializer.Serialize(ReadSettings(), JsonOpts);

    public void SetSettingsJson(string json)
    {
        if (_host is null) return;
        var merged = ReadSettings();
        var styleChanged = false;
        var voiceOrSpeedChanged = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name == "tts.style") styleChanged = true;
                else if (p.Name is "tts.voice" or "tts.speed") voiceOrSpeedChanged = true;
                merged[p.Name] = JsonSerializer.SerializeToElement(p.Value);
            }
        }
        catch (Exception ex)
        {
            _host.Log($"[tts] 设置解析失败：{ex.Message}");
            return;
        }
        if (styleChanged)
        {
            // 风格切换落盘：预设的音色/语速写回（对话框重开后可见；诚实映射，不虚构引擎能力）
            var style = StyleId(merged);
            var preset = Presets.FirstOrDefault(p => p.Id == style);
            if (preset.Id is not null && preset.Id != "custom")
            {
                merged["tts.voice"] = JsonSerializer.SerializeToElement(preset.Voice);
                merged["tts.speed"] = JsonSerializer.SerializeToElement((double)preset.Speed);
            }
        }
        else if (voiceOrSpeedChanged)
        {
            // 手动调音色/语速 → 风格自动转"自定义"（doc/Voice/07 §4.2：否则非自定义风格会无视手动音色）
            merged["tts.style"] = JsonSerializer.SerializeToElement(L("自定义", "Custom", "Kustom"));
        }
        _host.Settings.SetSettingsJson(JsonSerializer.Serialize(merged, JsonOpts));
    }

    public IReadOnlyList<string> Actions { get; } =
    [
        "install-model", "model-ready", "run-self-check", "test-voice", "remove-model",
    ];

    public async Task<PluginActionResult> RunActionAsync(string action, string? argumentsJson = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_store is null || _engine is null || _player is null) return new PluginActionResult(false, "plugin-not-initialized");
        switch (action)
        {
            case "install-model":
            {
                _engine.Reset();
                var (ok, msg) = await _store.InstallAsync(TtsModelStore.KokoroZhV11Int8, progress, cancellationToken).ConfigureAwait(false);
                if (ok) _engine.Bind(_store.GroupDir());
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
                progress?.Report(L("引擎自检（合成静音样本）…", "Engine self-check (silent synth)…", "Pemeriksaan mesin…"));
                var sw = Stopwatch.StartNew();
                var (synthOk, synthMs, error) = await SelfCheckAsync(cancellationToken).ConfigureAwait(false);
                if (!synthOk) return new PluginActionResult(false, $"{L("引擎自检失败", "Engine self-check failed", "Pemeriksaan mesin gagal")}: {error}");
                return new PluginActionResult(true, L($"引擎自检通过（合成 {synthMs}ms）",
                    $"Engine OK (synth {synthMs}ms)", $"Mesin OK (sintesis {synthMs}ms)"));
            }
            case "test-voice":
            {
                var text = _host?.Language switch
                {
                    "en" => "Hi, your task is complete and the report is ready for you to review.",
                    "id" => "Hai, tugas Anda telah selesai dan laporan siap untuk ditinjau.",
                    _ => "你好，你的任务已经完成，报告已经生成，可以打开查看。",
                };
                var result = await SpeakAsync(new TtsSpeakRequest { Text = text }, cancellationToken).ConfigureAwait(false);
                return result.Success
                    ? new PluginActionResult(true, L($"试听完成（{result.AudioMs}ms 音频）", $"Preview done ({result.AudioMs}ms audio)", $"Pratinjau selesai ({result.AudioMs}ms)"))
                    : new PluginActionResult(false, L($"试听失败：{result.ErrorCode}", $"Preview failed: {result.ErrorCode}", $"Pratinjau gagal: {result.ErrorCode}"));
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

    /// <summary>
    /// 语义分段（流水线用，doc/Voice/07 §8 逐句流式）：先按句末标点（。！？；!?;\n）切句——
    /// 这是语义句边界；超 48 字的长句在逗号/顿号处二次切分（就近贪心切，无逗号不硬切——伤韵律）；
    /// 碎段（&lt;8 字）并入前邻段。段数不设上限（RTF&lt;1 时后段合成总能藏进前段播放，
    /// 段越细首声越快、越接近逐句流式），硬上限 12 防极端文本把打断粒度切太碎。
    /// </summary>
    internal static List<string> SplitSentences(string text)
    {
        const int LongSentence = 48;
        const int MinPiece = 8;
        const int MaxParts = 12;

        var sentences = new List<string>();
        var start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '。' or '！' or '？' or '；' or '!' or '?' or ';' or '\n')
            {
                var seg = text[start..(i + 1)].Trim();
                if (seg.Length > 0) sentences.Add(seg);
                start = i + 1;
            }
        }
        var tail = text[start..].Trim();
        if (tail.Length > 0) sentences.Add(tail);
        if (sentences.Count == 0) sentences.Add(text);

        // 超长句二次切分：在逗号/顿号处就近切（让每段 ≤~48 字）
        var parts = new List<string>();
        foreach (var s in sentences)
        {
            if (s.Length <= LongSentence) { parts.Add(s); continue; }
            var segStart = 0;
            var lastBreak = -1;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] is '，' or '、' or ',') lastBreak = i;
                if (i - segStart + 1 >= LongSentence && lastBreak >= segStart + MinPiece)
                {
                    parts.Add(s[segStart..(lastBreak + 1)].Trim());
                    segStart = lastBreak + 1;
                    lastBreak = -1;
                }
            }
            var rest = s[segStart..].Trim();
            if (rest.Length > 0) parts.Add(rest);
        }

        // 碎段并入前邻段
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            if (parts.Count > 1 && parts[i].Length < MinPiece)
            {
                if (i == 0) parts[1] = parts[0] + parts[1];
                else parts[i - 1] += parts[i];
                parts.RemoveAt(i);
            }
        }

        // 极端文本兜底：段数超限则把最短段并入相邻长段
        while (parts.Count > MaxParts)
        {
            int m = 0;
            for (int i = 1; i < parts.Count; i++)
                if (parts[i].Length < parts[m].Length) m = i;
            int neighbor = m == parts.Count - 1 ? m - 1 : m + 1;
            if (neighbor < m) parts[neighbor] += parts[m];
            else parts[neighbor] = parts[m] + parts[neighbor];
            parts.RemoveAt(m);
        }
        return parts;
    }

    private async Task<(bool Ok, int SynthMs, string? Error)> SelfCheckAsync(CancellationToken ct)
    {
        if (_engine is null || _store?.IsInstalledQuick() != true) return (false, 0, "model-missing");
        _engine.Bind(_store.GroupDir());
        try
        {
            var sw = Stopwatch.StartNew();
            var (_, samples) = await Task.Run(() => _engine.Generate("语音引擎自检。", 1.0f, 3, ct), ct).ConfigureAwait(false);
            return (samples.Length > 0, (int)sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            _host?.Log($"[tts] 自检异常：{ex.Message}");
            return (false, 0, ex.GetType().Name);
        }
    }

    private (string Voice, float Speed, float Volume) ResolveVoiceSpeedVolume(TtsSpeakRequest request)
    {
        var style = StyleId(ReadSettings());
        string voice;
        float speed;
        if (request.Voice is not null)
        {
            voice = request.Voice;
            speed = request.Speed ?? ReadSpeed();
        }
        else if (style != "custom")
        {
            var preset = Presets.FirstOrDefault(p => p.Id == style);
            var storedVoice = ReadString("tts.voice") ?? "zf_024";
            if (preset.Id is null || string.IsNullOrEmpty(preset.Voice))
            {
                voice = Voices[0].Id;
                speed = request.Speed ?? ReadSpeed();
            }
            else if (storedVoice != preset.Voice && Voices.Any(v => v.Id == storedVoice))
            {
                // 存量手动音色（1.0.2 及之前存的）：存储音色 ≠ 预设音色 → 按自定义执行
                //（修复"切音色无效"；选回预设风格时 SetSettingsJson 会把存储值改写回预设，走上一分支）
                voice = storedVoice;
                speed = request.Speed ?? ReadSpeed();
            }
            else
            {
                voice = preset.Voice;
                speed = request.Speed ?? preset.Speed;
            }
        }
        else
        {
            voice = ReadString("tts.voice") ?? "zf_024";
            speed = request.Speed ?? ReadSpeed();
        }
        var volume = request.Volume ?? (float)(ReadDouble("tts.volume", 80.0) / 100.0);
        return (voice, Math.Clamp(speed, 0.7f, 1.5f), Math.Clamp(volume, 0f, 1f));

        float ReadSpeed() => (float)Math.Clamp(ReadDouble("tts.speed", 1.0), 0.7, 1.5);
    }

    private string StyleId(Dictionary<string, JsonElement> s)
    {
        var raw = s.TryGetValue("tts.style", out var v) ? v.ToString() : null;
        return raw switch
        {
            "自然" or "Natural" or "Alami" => "natural",
            "沉稳" or "Steady" or "Tenang" => "steady",
            "活泼" or "Lively" or "Ceria" => "lively",
            "自定义" or "Custom" or "Kustom" => "custom",
            _ => raw is not null && Presets.Any(p => p.Id == raw) ? raw : "natural",
        };
    }

    private string EngineStatusText() =>
        !_initialized ? L("插件未初始化", "Plugin not initialized", "Plugin belum diinisialisasi")
        : _store?.IsInstalledQuick() != true ? L("模型未安装——安装后可用", "Model not installed — install to use", "Model belum terpasang — pasang untuk dipakai")
        : L("已就绪", "Ready", "Siap");

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

    private bool ReadBool(string key, bool def)
    {
        var s = ReadSettings();
        return s.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;
    }

    private string? ReadString(string key)
    {
        var s = ReadSettings();
        return s.TryGetValue(key, out var v) ? v.ToString() : null;
    }

    private double ReadDouble(string key, double def)
    {
        var s = ReadSettings();
        return s.TryGetValue(key, out var v) && v.TryGetDouble(out var d) ? d : def;
    }

    private string L(string zh, string en, string id) => (_host?.Language) switch
    {
        "en" => en,
        "id" => id,
        _ => zh,
    };
}
