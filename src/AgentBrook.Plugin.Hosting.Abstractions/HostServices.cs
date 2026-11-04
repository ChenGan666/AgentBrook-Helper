using AgentBrook.Runtime.Abstractions;

namespace AgentBrook.Plugin.Hosting.Abstractions;

/// <summary>
/// 通用服务契约（设计 §15.2 服务清单的 P0 集合）。S1 只定义契约形状；
/// S3 由 AgentBrook.Plugin.Hosting 提供受控实现。插件拿不到 Controller/DI 容器/im.db 连接。
/// </summary>

/// <summary>插件命名空间状态存储：KV + 事务边界由实现保证；协议态（游标等）也校验活动代次（H03/S3 任务6）。</summary>
public interface IPluginStateStore
{
    /// <summary>命名空间内读写（命名空间 = PluginId+InstanceId，宿主强制前缀）。</summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, string value, CancellationToken cancellationToken);
    Task RemoveAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Blob 服务：流式读写、配额、保留期；返回宿主签发 ContentRef；不暴露绝对路径（H06）。</summary>
public interface IBlobService
{
    /// <summary>写入内容并取得内容引用。用途（purpose）参与授权（绑定 实例+用途+引用ID+有效期）。</summary>
    Task<ContentRef> WriteAsync(string purpose, Stream content, long maxBytes, string fileName, string? mediaType, CancellationToken cancellationToken);

    /// <summary>按授权读取。授权不匹配 / 引用不存在 → 抛 InvalidOperationException（禁止静默返回他命名空间数据）。</summary>
    Task<Stream> OpenReadAsync(ContentRef reference, string purpose, CancellationToken cancellationToken);
}

/// <summary>凭据服务：secret 引用 + 受控注入（Bearer/Header/URL 拼装与日志隐藏；设计 §15.3）。</summary>
public interface ICredentialService
{
    /// <summary>保存 secret（宿主持久化，值不回读给插件；返回引用）。</summary>
    Task<string> SetSecretAsync(string name, string value, CancellationToken cancellationToken);

    /// <summary>受控注入：GET/POST 时把 token 按指定方式注入（含 Telegram 式 URL 路径拼装），日志自动隐藏。</summary>
    SecretInjection InjectionFor(string secretRef, SecretPlacement placement);

    /// <summary>
    /// 校验外部提供的凭据值（如桥 inbound 的 X-Im-Bridge-Token 验签，S5-B）。
    /// 常数时间比较；引用不属于本命名空间 → false（不抛）。
    /// </summary>
    bool Verify(string secretRef, string providedValue);
}

/// <summary>secret 注入方式（覆盖 Header 与 URL 路径两类；S3 任务4）。</summary>
public sealed record SecretInjection(string SecretRef, SecretPlacement Placement);

public enum SecretPlacement
{
    None = 0,
    BearerHeader = 1,
    HeaderValue = 2,      // 配合 HeaderName
    UrlPathSegment = 3,   // Telegram: /bot<token>/<method>
    QueryParameter = 4,   // 配合 QueryName（慎用，仅平台强制时）
}

/// <summary>受控网络服务：outboundHosts 申报范围内放行；token 不跨源透传（H08）；不替代披露授权。</summary>
public interface INetworkService
{
    Task<HttpResult> SendAsync(HttpRequestSpec request, CancellationToken cancellationToken);

    /// <summary>
    /// 带 URL 路径 token 注入的发送（平台 token-in-path 语义，如 Telegram /bot&lt;token&gt;/&lt;method&gt;）。
    /// token 由宿主凭据服务解析并拼装，不出现在插件可见的任何文本/日志中（S5-T 任务4）。
    /// </summary>
    Task<HttpResult> SendWithUrlPathTokenAsync(string urlTemplate, string method, byte[]? body, string? contentType,
        SecretInjection tokenInjection, IReadOnlyList<HttpHeader>? headers, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record HttpRequestSpec(
    HttpMethod Method,
    string Url,
    IReadOnlyList<HttpHeader> Headers,
    byte[]? Body,
    IReadOnlyList<SecretInjection>? Injections,
    TimeSpan Timeout);

public sealed record HttpHeader(string Name, string Value);

public sealed record HttpResult(int StatusCode, byte[] Body, IReadOnlyList<string> RedirectChain,
    IReadOnlyDictionary<string, string>? Headers = null)   // 响应头（如微信 CDN 的 x-encrypted-param）
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>Webhook 服务：限定路由注册 + 请求大小/超时限制；平台签名验证由插件做（§15.2）。</summary>
public interface IWebhookService
{
    /// <summary>回环基址（http://127.0.0.1:{port}）——插件构造对外文件下载 URL 用（S5-B）。</summary>
    string BaseUrl { get; }

    /// <summary>注册回环路由；返回可撤销租约（禁用即撤销，旧回调失效）。</summary>
    IRegistrationLease RegisterRoute(string route, WebhookHandler handler, WebhookLimits limits);
}

public delegate Task<WebhookResponse> WebhookHandler(WebhookRequest request, CancellationToken cancellationToken);

public sealed record WebhookRequest(string Method, string Path, string Query, IReadOnlyList<HttpHeader> Headers, byte[] Body);

public sealed record WebhookResponse(int StatusCode, byte[] Body);

public sealed record WebhookLimits(long MaxBodyBytes, TimeSpan Timeout);

/// <summary>后台任务服务：启停绑定、取消、健康、资源计数（停用语义 §15.4）。</summary>
public interface IBackgroundTaskService
{
    IBackgroundTaskHandle Run(string name, Func<CancellationToken, Task> work);
    IReadOnlyList<IBackgroundTaskHandle> ActiveTasks { get; }
}

public interface IBackgroundTaskHandle
{
    string Name { get; }
    bool IsCompleted { get; }
    bool IsFaulted { get; }
    Task Completion { get; }
}

/// <summary>
/// 可撤销注册/活动租约：禁用撤销、卸载清理、更新切换；撤销后旧句柄调用即失效——
/// 不依赖插件"记得取消订阅"（设计 §15.2/§15.4）。
/// </summary>
public interface IRegistrationLease : IDisposable
{
    bool IsRevoked { get; }
    event Action? Revoked;
}

/// <summary>
/// 会话提交服务：默认拒绝授予给通道插件（双层授权 §15.2——裸会话提交是第二入口，
/// Messaging 准入完成后才以宿主签发的上下文调用）。
/// </summary>
public interface ISessionSubmitter
{
    /// <summary>提交受控回合。context 由宿主校验（Origin/Principal 不得取自插件数据）。</summary>
    Task<TurnOutcome> SubmitAsync(TurnExecutionContext context, string userText, CancellationToken cancellationToken);
}

/// <summary>审计日志：结构化事件 + 调用关联 ID；敏感值自动隐藏（H07）。</summary>
public interface IAuditLog
{
    void Write(string category, string eventId, IReadOnlyDictionary<string, string?> fields);
}
