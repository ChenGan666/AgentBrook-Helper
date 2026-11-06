using AgentBrook.Plugin.Abstractions;

namespace AgentBrook.Helper.Core.Voice;

/// <summary>
/// 转写服务解析链（doc/Voice/01 §3.1，B4 实装插件位）：本地插件（IAsrCapability）→ 云端 → 未配置。
/// - 插件位：accessor 返回 GetCapability&lt;IAsrCapability&gt;() 的结果（未装/停用 → null）；
///   ogg/opus 在插件调用前经 AudioFrontend 归一化为 16k PCM（D9：解码归宿主）。
/// - 插件转写失败（异常/失败结果）自动回落云端——停用插件瞬间进行中的转写也走此路径。
/// - 均不可用 → Error="not-configured"（调用方门卫据此回音）。
/// </summary>
public sealed class AsrService : IAsrService
{
    private readonly Func<IAsrCapability?> _pluginCapability;
    private readonly Func<CloudAsrProvider.CloudConfig?> _cloudConfig;
    private readonly CloudAsrProvider _cloud;

    public AsrService(Func<IAsrCapability?> pluginCapability, Func<CloudAsrProvider.CloudConfig?> cloudConfig,
        CloudAsrProvider? cloud = null)
    {
        _pluginCapability = pluginCapability;
        _cloudConfig = cloudConfig;
        _cloud = cloud ?? new CloudAsrProvider();
    }

    public bool IsConfigured =>
        _pluginCapability() is { IsReady: true } || _cloudConfig() is not null;

    public async Task<AsrResult> TranscribeAsync(AsrRequest request, CancellationToken ct)
    {
        var duration = TimeSpan.FromSeconds(request.Audio.Length / 2.0 / AudioFrontend.TargetSampleRate);

        // 1) 本地插件优先（IsReady 才走；og 需归一化为 PCM）
        if (_pluginCapability() is { IsReady: true } capability)
        {
            try
            {
                var pcm = AudioFrontend.IsDecodable(request.MediaType)
                    ? AudioFrontend.ToPcm16k(request.Audio, request.MediaType)
                    : request.Audio;
                if (pcm is null)
                    return AsrResult.Fail("plugin", "audio-undecodable");

                var r = await capability.TranscribeAsync(new AsrCapabilityRequest
                {
                    Pcm16kMono = pcm,
                    SampleRate = AudioFrontend.TargetSampleRate,
                    LanguageHint = request.LanguageHint,
                }, ct).ConfigureAwait(false);

                if (r.Success)
                    return AsrResult.Ok(r.Text, "plugin", duration);
                // 插件失败（如引擎异常）→ 回落云端；插件未初始化类硬失败也回落
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 外部取消不回落
            }
            catch (Exception)
            {
                // 插件崩溃 → 回落云端
            }
        }

        // 2) 云端
        if (_cloudConfig() is { } cloud)
            return await _cloud.TranscribeAsync(cloud, request, ct).ConfigureAwait(false);

        // 3) 未配置
        return AsrResult.Fail("", "not-configured");
    }
}
