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

/// <summary>插件入口发现标记：实现类型标注 [PluginEntry(id)]，宿主据此实例化。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class PluginEntryAttribute : Attribute
{
    public PluginEntryAttribute(string id) => Id = id;

    public string Id { get; }
}
