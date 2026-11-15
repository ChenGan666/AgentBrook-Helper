using Microsoft.Extensions.AI;

namespace AgentBrook.Plugin.Abstractions;

/// <summary>插件生命周期契约。实现类型标注 [PluginEntry(id)] 供宿主发现。</summary>
public interface IHelperPlugin
{
    /// <summary>插件唯一 id（与服务器目录/manifest 一致，如 privacy-masking）。</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>插件版本（SemVer，与 manifest.version 一致）。</summary>
    string Version { get; }

    /// <summary>
    /// 插件图标（v1.1 新增）：单字符 emoji 或符号，显示在插件名称前（插件列表 / 设置标题等）。
    /// 默认空 = 不显示图标。建议使用单 emoji；跨平台字体覆盖有限的字符避免使用。
    /// </summary>
    string Icon => "";

    /// <summary>加载后初始化（读设置、预热）。抛异常 → 插件标记加载失败，不影响宿主。</summary>
    Task InitializeAsync(IPluginHostContext host, CancellationToken cancellationToken = default);

    /// <summary>停用/卸载前调用；实现必须幂等并尽快返回（停止后台活动、释放非托管资源）。</summary>
    Task ShutdownAsync();
}

/// <summary>宿主向插件注入的服务。</summary>
public interface IPluginHostContext
{
    /// <summary>工作区根目录（插件数据解析：{root}/plugins/{id}/、{root}/privacy/…）。</summary>
    string WorkspaceRoot { get; }

    /// <summary>界面语言（zh / en / id）——插件按此选择用户可见文案。</summary>
    string Language { get; }

    /// <summary>插件设置的宿主持有存储（值存 state.json；插件未加载时设置页仍可显示）。</summary>
    IPluginSettingsStore Settings { get; }

    /// <summary>
    /// 宿主统一资源下载服务（doc/18 下载服务标准）：与升级功能同源
    /// （同一 HttpClient/超时/进度/暂存策略，集中维护）。插件下载任何服务端资源
    /// （模型/数据包/清单）一律经此，禁止自建 HttpClient。
    /// </summary>
    IPluginDownloadService Download { get; }

    /// <summary>宿主日志出口。</summary>
    void Log(string message);

    /// <summary>
    /// 请求重建聊天客户端管线（热插拔）：插件设置中影响"是否装饰"的开关
    /// （如脱敏总开关）变化时调用；下次请求即换链，无需重启宿主。
    /// </summary>
    void RequestPipelineRebuild();
}

/// <summary>资源下载进度（Total = Content-Length，服务端未回长度为 null）。</summary>
public sealed record PluginDownloadProgress(long Bytes, long? Total);

/// <summary>
/// 宿主统一下载服务：大文件分块落盘（同目录 .part 暂存，成功后原子改名，失败自清），
/// 每 ≥256KB 上报一次进度；字符串下载用于索引/清单等小文件。
/// </summary>
public interface IPluginDownloadService
{
    Task DownloadFileAsync(string url, string destPath,
        IProgress<PluginDownloadProgress>? progress = null, CancellationToken cancellationToken = default);

    Task<string> DownloadStringAsync(string url, CancellationToken cancellationToken = default);
}

/// <summary>插件设置的宿主持有存储（JSON 字符串读写，结构由插件自定义）。</summary>
public interface IPluginSettingsStore
{
    string GetSettingsJson();

    void SetSettingsJson(string json);
}

/// <summary>能力接口标记（宿主按能力发现/聚合）。</summary>
public interface IPluginCapability { }

/// <summary>能力：装饰模型客户端管道（隐私脱敏壳 = 首个实现）。</summary>
public interface IChatClientDecorator : IPluginCapability
{
    /// <summary>返回装饰后的客户端；null = 本插件当前不装饰（如总开关关闭）。</summary>
    IChatClient? Decorate(IChatClient inner);
}

/// <summary>回合脱敏遥测（T5.3 徽标/悬停数据源）：本回合出站脱敏计数与模型实际收到的最后一条用户文本。</summary>
public sealed record PluginTurnStats(
    int MaskedCount,
    string? MaskedLastUserText,
    bool ImagesPresent,
    bool PlaceholderNoteInjected);

/// <summary>回合遥测能力（可选能力接口）：宿主在回合结束后查询最近一次的脱敏统计。</summary>
public interface IPluginTurnTelemetry : IPluginCapability
{
    /// <summary>最近一个回合的统计；尚无回合时返回 null。</summary>
    PluginTurnStats? GetLastTurnStats();
}

/// <summary>
/// 显示文本还原能力（可选能力接口，v1.0.9 设计修订）：流式期间占位符原样输出，
/// 回合结束后宿主对最终文本做一次性还原（doc/15 §7.3 修订——替代逐 chunk 流式回扣）。
/// </summary>
public interface IDisplayTextRestorer : IPluginCapability
{
    /// <summary>还原文本中的脱敏标记；无活跃会话映射时原样返回。</summary>
    string RestoreDisplayText(string text);
}

/// <summary>具名操作结果。</summary>
public readonly record struct PluginActionResult(bool Success, string Message);

/// <summary>能力：向设置页贡献声明式设置 + 具名操作。</summary>
public interface ISettingsContributor : IPluginCapability
{
    /// <summary>声明式设置 schema（JSON：toggle/choice/slider/text/action + 可见性条件），宿主据此渲染。</summary>
    string SettingsSchemaJson();

    /// <summary>当前值（JSON；存宿主 state.json，插件未加载时设置页仍可显示）。</summary>
    string GetSettingsJson();

    /// <summary>写回设置值（宿主持久化；插件内部自行同步生效）。</summary>
    void SetSettingsJson(string json);

    /// <summary>可用具名操作（如 install-model / run-self-check / list-terms / add-term），宿主渲染为按钮。</summary>
    IReadOnlyList<string> Actions { get; }

    /// <summary>执行具名操作；argumentsJson 为操作参数（JSON，如词典词条），进度经 progress 回报。</summary>
    Task<PluginActionResult> RunActionAsync(string action, string? argumentsJson = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

// ── 语音转写能力（v1.1 additive，doc/Voice/02 §4.1）────────────────────────
// 宿主经 PluginRegistry.GetCapability<IAsrCapability>() 发现第一个已启用实现；
// 契约对象一律属性式（评审纪律：共享契约新类型禁位置参数，防演进期位置破坏）。

/// <summary>本地语音转写能力（可选能力接口）：插件提供离线 ASR 引擎（如 sherpa-onnx + SenseVoice）。</summary>
public interface IAsrCapability : IPluginCapability
{
    /// <summary>引擎描述（设置页展示，如 "SenseVoice-Small (本地)"）。未就绪返回 null。</summary>
    string? DescribeEngine();

    /// <summary>是否就绪（模型已下载且自检通过）。</summary>
    bool IsReady { get; }

    /// <summary>转写 PCM 音频。解码归一化由宿主负责（16kHz 单声道；doc/Voice D9：插件只吃 PCM）。</summary>
    Task<AsrCapabilityResult> TranscribeAsync(AsrCapabilityRequest request, CancellationToken cancellationToken = default);
}

/// <summary>转写请求。采样率默认 16kHz 单声道 PCM。</summary>
public sealed class AsrCapabilityRequest
{
    /// <summary>PCM 音频字节（16bit LE 交错；单声道）。</summary>
    public required ReadOnlyMemory<byte> Pcm16kMono { get; init; }

    /// <summary>采样率（Hz）。默认 16000。</summary>
    public int SampleRate { get; init; } = 16000;

    /// <summary>语言提示（如 "zh"/"en"）；null=引擎自动检测。</summary>
    public string? LanguageHint { get; init; }
}

/// <summary>转写结果。失败时 Success=false 且 Error 说明原因；音频字节不回传（脱敏红线）。</summary>
public sealed class AsrCapabilityResult
{
    /// <summary>是否成功。</summary>
    public required bool Success { get; init; }

    /// <summary>转写文本（已剥特殊标签、含标点）。失败时为空串。</summary>
    public string Text { get; init; } = "";

    /// <summary>失败原因（Success=false 时供日志/回音）。</summary>
    public string? Error { get; init; }

    /// <summary>音频时长（供计费/遥测）。</summary>
    public TimeSpan AudioDuration { get; init; }
}

/// <summary>插件入口发现标记：实现类型标注 [PluginEntry(id)]，宿主据此实例化。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class PluginEntryAttribute : Attribute
{
    public PluginEntryAttribute(string id) => Id = id;

    public string Id { get; }
}

// ── 语音播报能力（v1.2 additive，doc/Voice/07 §5.2）────────────────────────
// 宿主经 PluginRegistry.GetCapability<ITtsCapability>() 发现第一个已启用实现；
// 契约对象一律属性式（红线 4）；宿主只在「回合完成自动播报」与设置页两个入口消费。

/// <summary>播报语言（引擎 v1 只支持中/英；id 语 UI → 宿主跳过播报）。</summary>
public enum TtsAnnounceLanguage
{
    /// <summary>跟随界面语言 host.Language（zh/en 直播；id → 跳过）。</summary>
    FollowUi = 0,
    Chinese = 1,
    English = 2,
}

/// <summary>本地语音播报能力（可选能力接口）：插件提供离线 TTS 引擎（合成 + 播放一体，单槽互斥）。</summary>
public interface ITtsCapability : IPluginCapability
{
    /// <summary>引擎与模型状态（设置页"引擎状态"行 + 宿主日志用）。永不抛异常。</summary>
    TtsEngineStatus DescribeEngine();

    /// <summary>是否就绪（模型已安装且引擎可初始化；不要求引擎已加载——懒加载）。</summary>
    bool IsReady { get; }

    /// <summary>「任务完成后自动播报」总开关（宿主自动播报链的门卫之一；试听/手动 Speak 不受此限）。</summary>
    bool AutoAnnounceEnabled { get; }

    /// <summary>仅窗口后台时播报（true=默认；false=总是播报）。</summary>
    bool AnnounceOnlyWhenInactive { get; }

    /// <summary>自动播报的摘要语言（宿主传给摘要提示词；引擎不支持的语言宿主跳过播报）。</summary>
    TtsAnnounceLanguage AnnounceLanguage { get; }

    /// <summary>
    /// 合成并播放（需求"工具一"）。单槽语义：InterruptCurrent=true 时先停当前播报；
    /// 合成/播放全程后台线程，失败以结果对象返回、绝不抛出宿主。Text 建议为纯文本（无 Markdown）。
    /// </summary>
    Task<TtsSpeakResult> SpeakAsync(TtsSpeakRequest request, CancellationToken cancellationToken = default);

    /// <summary>停止当前播报（需求"工具二"；幂等，无播放也安全）。</summary>
    Task StopAsync();
}

/// <summary>引擎状态描述（DescribeEngine 返回）。</summary>
public sealed class TtsEngineStatus
{
    public required bool Ready { get; init; }

    /// <summary>引擎/模型组展示名（如 "Kokoro v1.1 zh (int8, 本地)"）。未就绪为 null。</summary>
    public string? EngineName { get; init; }

    /// <summary>未就绪原因的错误码（tts.model.missing 等；文案由消费方按语言渲染）。</summary>
    public string? ErrorCode { get; init; }

    /// <summary>可用音色清单（模型安装后非空）。</summary>
    public IReadOnlyList<TtsVoiceInfo> Voices { get; init; } = [];
}

/// <summary>音色描述（出厂精选表驱动；模型内实际音色数可能多于表）。</summary>
public sealed class TtsVoiceInfo
{
    /// <summary>音色标识（voices.bin sid 或名称键）。</summary>
    public required string Id { get; init; }

    /// <summary>展示名（已按 host.Language 三语化）。</summary>
    public required string Name { get; init; }

    /// <summary>风格组（natural/steady/lively；null=未分组）。</summary>
    public string? Preset { get; init; }
}

/// <summary>播报请求。Voice/Speed/Volume 为 null 时用插件当前设置值。</summary>
public sealed class TtsSpeakRequest
{
    /// <summary>要朗读的纯文本（无 Markdown/代码/链接；建议 ≤120 字）。</summary>
    public required string Text { get; init; }

    /// <summary>音色（音色 Id）；null=当前设置。</summary>
    public string? Voice { get; init; }

    /// <summary>语速 0.7~1.5；null=当前设置。</summary>
    public float? Speed { get; init; }

    /// <summary>音量 0~1（播放器增益单路径）；null=当前设置。</summary>
    public float? Volume { get; init; }

    /// <summary>打断当前播报（默认 true——单槽语义）。</summary>
    public bool InterruptCurrent { get; init; } = true;
}

/// <summary>播报结果。失败时 Success=false 且 ErrorCode 为稳定码（宿主/日志消费，不弹 UI）。</summary>
public sealed class TtsSpeakResult
{
    public required bool Success { get; init; }

    /// <summary>稳定错误码：tts.model.missing / tts.engine.init / tts.synth / tts.play.device / tts.cancelled / tts.empty-text …</summary>
    public string? ErrorCode { get; init; }

    /// <summary>错误详情（按 host.Language 的描述；仅日志用）。</summary>
    public string? ErrorText { get; init; }

    /// <summary>临时 wav 路径（播完即删的短暂物化；返回时仍存在，供取证）。</summary>
    public string? WavPath { get; init; }

    /// <summary>音频时长（ms）。</summary>
    public int AudioMs { get; init; }

    /// <summary>合成耗时（ms，性能日志）。</summary>
    public int SynthMs { get; init; }
}
