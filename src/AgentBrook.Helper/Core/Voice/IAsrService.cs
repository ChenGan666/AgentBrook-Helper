namespace AgentBrook.Helper.Core.Voice;

/// <summary>
/// 语音转写服务（doc/Voice/01 §3.1）：IM 语音识别 / 桌面语音输入共用的唯一门面。
/// Provider 解析链在 AsrService：本地插件（IAsrCapability）→ 云端（OpenAI 兼容）→ 未配置（门卫回音）。
/// </summary>
public interface IAsrService
{
    /// <summary>是否具备任一可用转写引擎（能力门卫/设置页 Ready 判定）。</summary>
    bool IsConfigured { get; }

    /// <summary>转写。未配置时返回 Error="not-configured"；永不抛出引擎异常（错误打包进结果）。</summary>
    Task<AsrResult> TranscribeAsync(AsrRequest request, CancellationToken ct);
}
