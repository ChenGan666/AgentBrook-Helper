using System.Text;
using System.Text.Json;
using AgentBrook.Messaging.Abstractions;
using AgentBrook.Plugin.Hosting.Abstractions;
using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Plugin.Im.Weixin;

/// <summary>
/// 微信 iLink 扫码登录（**协议实现属于插件**，宿主只通过 <see cref="IInteractiveLoginProvider"/> 驱动）：
/// 固定入口取二维码 → 状态长轮询 →（可选）数字配对码 → confirmed 时把 bot_token 写入宿主凭据库，
/// 返回 secretRef + baseurl + ilink_bot_id 供宿主写入通道配置。
/// 登录实例与通道实例共用命名空间（im-weixin+inst-1），故 secretRef 可被通道直接使用。
/// </summary>
public sealed class WeixinLoginProvider : IInteractiveLoginProvider, ICapabilityInstance
{
    /// <summary>固定登录入口（协议 §2.5.1：首取二维码始终用该域名）。</summary>
    internal const string LoginBaseUrl = "https://ilinkai.weixin.qq.com";

    private readonly IExtensionContext _services;
    private readonly string _baseUrl;
    private LoginState _state = new("", "", "");

    private sealed record LoginState(string Qrcode, string BotTokenHint, string StatusBaseUrl);

    public WeixinLoginProvider(InstanceRef instance, ActivationId activation, IExtensionContext services,
        string? statusBaseUrl = null)
    {
        Instance = instance;
        Activation = activation;
        _services = services;
        _baseUrl = string.IsNullOrWhiteSpace(statusBaseUrl) ? LoginBaseUrl : statusBaseUrl!;
    }

    public InstanceRef Instance { get; }
    public ActivationId Activation { get; }

    /// <summary>登录提供者无后台资源（状态全在服务端，按 qrcode 键控）：Dispose 直接完成。</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<LoginChallenge> BeginAsync(CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { local_token_list = Array.Empty<string>() });
        var http = await PostAsync($"/ilink/bot/get_bot_qrcode?bot_type=3", body, cancellationToken).ConfigureAwait(false);
        if (http is null || !http.IsSuccess)
            throw new InvalidOperationException(
                $"[{ImErrorCodes.Temporary}] 获取微信登录二维码失败（{Describe(http)}）");
        using var doc = JsonDocument.Parse(http.Body);
        var root = doc.RootElement;
        var qrcode = root.TryGetProperty("qrcode", out var q) ? q.GetString() : null;
        var content = root.TryGetProperty("qrcode_img_content", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(qrcode))
            throw new InvalidOperationException(
                $"[{ImErrorCodes.BadPayload}] 二维码响应缺少 qrcode（{Preview(http.Body)}）");
        _state = new LoginState(qrcode!, "", _baseUrl);
        _ = content;   // 内容可能等于 qrcode 本身；宿主用它编码二维码
        return new LoginChallenge(qrcode!, content ?? qrcode!,
            "请用手机微信扫码，并在手机上确认登录（可能要求输入数字配对码）。");
    }

    public async Task<LoginPoll> PollAsync(string challengeId, string? verifyCode, CancellationToken cancellationToken)
    {
        var statusBase = string.IsNullOrWhiteSpace(_state.StatusBaseUrl) ? _baseUrl : _state.StatusBaseUrl;
        var url = $"{statusBase.TrimEnd('/')}/ilink/bot/get_qrcode_status?qrcode={Uri.EscapeDataString(challengeId)}";
        if (!string.IsNullOrWhiteSpace(verifyCode))
            url += "&verify_code=" + Uri.EscapeDataString(verifyCode);

        var http = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (http is null)
            return new LoginPoll(LoginStage.Failed, "网络不可达（请检查网络后重试）");
        if (!http.IsSuccess && http.StatusCode is not (504 or 524))
            return new LoginPoll(LoginStage.Failed, $"HTTP {http.StatusCode}");

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(http.Body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new LoginPoll(LoginStage.Failed, "状态响应不可解析");
        }

        var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        switch (status)
        {
            case "wait":
                return new LoginPoll(LoginStage.Waiting);
            case "scaned":
                return new LoginPoll(LoginStage.Scanned);
            case "need_verifycode":
                return new LoginPoll(LoginStage.NeedVerification);
            case "verify_code_blocked":
                return new LoginPoll(LoginStage.Blocked, "配对码多次错误，请重新发起登录");
            case "expired":
                return new LoginPoll(LoginStage.Expired, "二维码已过期，请重新发起登录");
            case "scaned_but_redirect":
            {
                var host = root.TryGetProperty("redirect_host", out var rh) ? rh.GetString() : null;
                if (string.IsNullOrWhiteSpace(host)) return new LoginPoll(LoginStage.Scanned);
                _state = _state with { StatusBaseUrl = "https://" + host };
                return new LoginPoll(LoginStage.Redirected, RedirectBaseUrl: "https://" + host);
            }
            case "binded_redirect":
                // 账号已绑定本客户端：沿用本地 token（若尚无 token，提示重新扫码）
                return new LoginPoll(LoginStage.Failed, "该微信已绑定到本客户端的旧登录，请重新扫码以刷新凭据");
            case "confirmed":
            {
                var token = root.TryGetProperty("bot_token", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(token))
                    return new LoginPoll(LoginStage.Failed, "confirmed 但未返回 bot_token");
                var botId = root.TryGetProperty("ilink_bot_id", out var b) ? b.GetString() : null;
                var userName = root.TryGetProperty("ilink_user_id", out var u) ? u.GetString() : null;
                var baseUrl = root.TryGetProperty("baseurl", out var bu) ? bu.GetString() : null;

                // 凭据落库（插件命名空间 = im-weixin+inst-1，与通道实例一致）
                var credentials = _services.GetService<ICredentialService>()
                    ?? throw new InvalidOperationException($"[{ImErrorCodes.ServiceNotGranted}] 凭据服务未授予。");
                var secretRef = await credentials.SetSecretAsync("bot-token", token!, cancellationToken)
                    .ConfigureAwait(false);
                return new LoginPoll(LoginStage.Confirmed, Completion: new LoginCompletion(
                    secretRef, baseUrl, botId, userName));
            }
            default:
                return new LoginPoll(LoginStage.Waiting, string.IsNullOrEmpty(status) ? Preview(http.Body) : status);
        }
    }

    // ── HTTP（登录前无 token；仍走宿主受控网络）──

    private async Task<HttpResult?> PostAsync(string path, string body, CancellationToken ct)
    {
        var network = _services.GetService<INetworkService>()
            ?? throw new InvalidOperationException($"[{ImErrorCodes.ServiceNotGranted}] 网络服务未授予。");
        try
        {
            // 不把取消令牌交给 HttpClient（理由同 WeixinChannel.SendPostAsync：取消不应产生异常）
            var timeout = TimeSpan.FromSeconds(30);
            var sendTask = network.SendAsync(new HttpRequestSpec(
                HttpMethod.Post, _baseUrl + path, WeixinProtocol.PostHeaders(),
                Encoding.UTF8.GetBytes(body), [], timeout), CancellationToken.None);
            var finished = await Task.WhenAny(sendTask, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (finished != sendTask)
            {
                _ = sendTask.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                return null;
            }
            return await sendTask.ConfigureAwait(false);
        }
        catch { return null; }
    }

    private async Task<HttpResult?> GetAsync(string url, CancellationToken ct)
    {
        var network = _services.GetService<INetworkService>()
            ?? throw new InvalidOperationException($"[{ImErrorCodes.ServiceNotGranted}] 网络服务未授予。");
        try
        {
            var timeout = TimeSpan.FromSeconds(40);
            var sendTask = network.SendAsync(new HttpRequestSpec(
                HttpMethod.Get, url, WeixinProtocol.GetHeaders(), null, [], timeout), CancellationToken.None);
            var finished = await Task.WhenAny(sendTask, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (finished != sendTask)
            {
                _ = sendTask.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                return null;
            }
            return await sendTask.ConfigureAwait(false);
        }
        catch { return null; }
    }

    private static string Describe(HttpResult? http) =>
        http is null ? "网络不可达" : $"HTTP {http.StatusCode}：{Preview(http.Body)}";

    private static string Preview(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        return text.Length > 160 ? text[..160] + "…" : text;
    }
}
