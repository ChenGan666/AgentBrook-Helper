using OpenAI;
using OpenAI.Audio;

namespace AgentBrook.Helper.Core.Voice;

/// <summary>
/// 云端转写 Provider（doc/Voice/01 §3.1）：OpenAI 兼容 /v1/audio/transcriptions
/// （OpenAI SDK 2.14.0 AudioClient，内建指数退避重试与 chat 通道同源——网络重试审计结论）。
/// 供应商 ogg 支持矩阵：OpenAI 官方可直传；国产兼容端点不一——失败自动降级 PCM→wav 重传一次
/// （AudioFrontend 解码，D9 语义统一）。配置来源 = ProviderConfig.ActiveAsrModel 所在供应商
/// （与 chat 激活供应商无关）；Protocol 须 ∈ {空, "openai"}（设置层校验）。
/// </summary>
public sealed class CloudAsrProvider
{
    /// <summary>云端配置：转写模型挂哪个 provider 就用谁的 BaseUrl/ApiKey。</summary>
    public sealed record CloudConfig(string BaseUrl, string ApiKey, string Model);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);   // doc 01 A2：30s 上限

    public async Task<AsrResult> TranscribeAsync(CloudConfig config, AsrRequest request, CancellationToken ct)
    {
        // 30s 超时用独立 CTS（不取消调用方的 ct——区分"超时"与"外部取消"）
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);
        try
        {
            var text = await TranscribeOnceAsync(config, request, timeoutCts.Token).ConfigureAwait(false);
            return AsrResult.Ok(text, "cloud", TimeSpan.FromSeconds(request.Audio.Length / 2.0 / AudioFrontend.TargetSampleRate));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return AsrResult.Fail("cloud", "timeout");
        }
        catch (OperationCanceledException)
        {
            return AsrResult.Fail("cloud", "cancelled");
        }
        catch (Exception ex)
        {
            // 首次失败且原始格式可转 wav → 降级重传一次（供应商 ogg 支持矩阵，doc 01 A2）
            var status = ClientStatus(ex);
            if (status is >= 400 and < 500 and not 401 and not 429
                && AudioFrontend.IsDecodable(request.MediaType))
            {
                var pcm = AudioFrontend.ToPcm16k(request.Audio, request.MediaType);
                if (pcm is not null)
                {
                    try
                    {
                        var wavRequest = request with { Audio = AudioFrontend.WrapWav(pcm), MediaType = "audio/wav" };
                        var text = await TranscribeOnceAsync(config, wavRequest, timeoutCts.Token).ConfigureAwait(false);
                        return AsrResult.Ok(text, "cloud-wav", TimeSpan.FromSeconds(pcm.Length / 2.0 / AudioFrontend.TargetSampleRate));
                    }
                    catch (Exception ex2)
                    {
                        return AsrResult.Fail("cloud-wav", ClassifyError(ex2), ClientStatus(ex2));
                    }
                }
            }
            return AsrResult.Fail("cloud", ClassifyError(ex), status);
        }
    }

    /// <summary>单次转写。SDK 2.14 转写 options 无 Language 字段——语言靠模型自动检测。</summary>
    private static async Task<string> TranscribeOnceAsync(CloudConfig config, AsrRequest request, CancellationToken ct)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(NormalizeEndpoint(config.BaseUrl)) };
        options.RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(maxRetries: 1);   // 外层已有降级重试，内层少重试快速失败
        var client = new OpenAIClient(new System.ClientModel.ApiKeyCredential(config.ApiKey), options)
            .GetAudioClient(config.Model);

        var ext = AudioFrontend.ExtensionOf(request.MediaType);
        await using var stream = new MemoryStream(request.Audio, writable: false);
        var result = await client.TranscribeAudioAsync(stream, $"audio.{ext}", cancellationToken: ct).ConfigureAwait(false);
        return result.Value.Text?.Trim() ?? "";
    }

    /// <summary>端点规范化：与 AgentFactory（chat 通道）同规则——agentbrook 域补 /v1。</summary>
    internal static string NormalizeEndpoint(string baseUrl)
    {
        var url = (baseUrl ?? "").TrimEnd('/');
        return url.Contains("agentbrook", StringComparison.OrdinalIgnoreCase)
               && !url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? url + "/v1"
            : url;
    }

    /// <summary>HTTP 状态提取（沿异常链找 Status 属性；ClientResultException → raw response status）。</summary>
    internal static int? ClientStatus(Exception ex)
    {
        var cur = ex;
        while (cur is not null)
        {
            var statusProp = cur.GetType().GetProperty("Status");
            if (statusProp?.GetValue(cur) is int s) return s;
            cur = cur.InnerException;
        }
        return null;
    }

    /// <summary>错误分类（doc 01 A2）：timeout / cancelled / auth / rate-limit / http-{code} / network / unknown。</summary>
    internal static string ClassifyError(Exception ex)
    {
        if (ex is OperationCanceledException) return "cancelled";
        var status = ClientStatus(ex);
        return status switch
        {
            401 or 403 => "auth",
            429 => "rate-limit",
            >= 400 and < 500 => $"http-{status}",
            >= 500 => $"http-{status}",
            _ => ex.InnerException is not null || ex.GetType().Name.Contains("Http") ? "network" : "unknown",
        };
    }
}
