namespace AgentBrook.Messaging.Abstractions;

/// <summary>
/// 交互式登录提供者（契约，P1）：扫码/授权类通道（微信 iLink、未来的企微/公众号等）由**插件**实现协议细节，
/// 宿主只负责呈现与编排（渲染二维码、收集配对码、落配置、启动通道）。
/// 插件在 Confirmed 时自行把凭据写入宿主凭据库，并返回可供通道配置使用的 secretRef。
/// </summary>
public interface IInteractiveLoginProvider
{
    /// <summary>发起登录：返回挑战（二维码内容 qrcodeImgContent 等）与展示提示。</summary>
    Task<LoginChallenge> BeginAsync(CancellationToken cancellationToken);

    /// <summary>轮询登录状态；verifyCode 为手机端要求的数字配对码（无则 null）。</summary>
    Task<LoginPoll> PollAsync(string challengeId, string? verifyCode, CancellationToken cancellationToken);
}

/// <summary>登录挑战：QrContent 为需要编码成二维码的文本（URL/协议串），由宿主渲染。</summary>
public sealed record LoginChallenge(string Id, string QrContent, string? Instructions);

/// <summary>登录阶段（各通道语义映射到统一阶段）。</summary>
public enum LoginStage
{
    /// <summary>等待扫码。</summary>
    Waiting = 1,
    /// <summary>已扫码，等待手机端确认。</summary>
    Scanned = 2,
    /// <summary>需要数字配对码（或上次配对码错误）。</summary>
    NeedVerification = 3,
    /// <summary>配对码多次错误被限制：需重新发起。</summary>
    Blocked = 4,
    /// <summary>二维码/挑战已过期：需重新发起。</summary>
    Expired = 5,
    /// <summary>需要切换登录节点（携带 RedirectBaseUrl，宿主可重启提供者实例）。</summary>
    Redirected = 6,
    /// <summary>登录完成（携带 Completion）。</summary>
    Confirmed = 7,
    /// <summary>失败（Detail 说明原因）。</summary>
    Failed = 8,
}

/// <summary>轮询结果。</summary>
public sealed record LoginPoll(
    LoginStage Stage,
    string? Detail = null,
    string? RedirectBaseUrl = null,
    LoginCompletion? Completion = null);

/// <summary>
/// 登录完成：SecretRef 是插件已写入宿主凭据库的引用（宿主写入通道配置即可）；
/// BaseUrl/AccountId/AccountName 为通道运行所需参数（按通道语义可空）。
/// </summary>
public sealed record LoginCompletion(
    string SecretRef,
    string? BaseUrl = null,
    string? AccountId = null,
    string? AccountName = null);
