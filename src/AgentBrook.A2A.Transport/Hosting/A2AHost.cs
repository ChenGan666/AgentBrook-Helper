using System.Net;
using System.Net.Sockets;
using AgentBrook.A2A.Core.Artifacts;
using AgentBrook.A2A.Core.Identity;
using AgentBrook.A2A.Core.Persistence;
using AgentBrook.A2A.Core.Peers;
using AgentBrook.A2A.Core.Sessions;
using AgentBrook.A2A.Transport.Client;
using AgentBrook.A2A.Transport.Discovery;
using AgentBrook.A2A.Transport.Pairing;
using AgentBrook.A2A.Transport.Presence;
using AgentBrook.A2A.Transport.Protocol;
using AgentBrook.A2A.Transport.Security;
using A2A;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace AgentBrook.A2A.Transport.Hosting;

/// <summary>对端发来的普通消息（已通过签名鉴权并写入会话日志）。</summary>
public sealed record PeerMessageReceivedArgs(
    string SenderNodeId,
    string? SenderDisplayName,
    string? SenderIcon,
    string SessionId,
    string Text);

public sealed class A2AHost : IAsyncDisposable
{
    private readonly A2AHostOptions _options;
    private WebApplication? _app;
    private readonly CancellationTokenSource _stopCts = new();

    public A2AHostOptions Options => _options;
    public NodeIdentityService Identity { get; }
    public A2AStore Store { get; }
    public ArtifactStore ArtifactStore { get; }
    public PairingService Pairing { get; }
    public PresenceService Presence { get; }
    public A2AClientService Client { get; }
    public ILanDiscovery Discovery { get; }
    public AgentCard AgentCard { get; }

    /// <summary>
    /// 收到标准 A2A 消息时的业务钩子（参数：发送方 nodeId、消息文本；返回业务回执文本或 null）。
    /// 由组合根（A2AExtensionController / 业务示例服务）设置，网络层不直接实现业务。
    /// </summary>
    public Func<string, string, Task<string?>>? IncomingMessageHook { get; set; }

    /// <summary>
    /// 配对审批钩子（文档 §6.4 双端用户确认）：发起方与响应方在各自确认前都会被询问；
    /// 返回 false 或超时视为拒绝。未设置时保持旧行为（自动确认，测试/无 UI 宿主用）。
    /// </summary>
    public Func<PairingPrompt, Task<bool>>? PairingApprovalHook { get; set; }

    /// <summary>
    /// 收到对端普通消息（非业务消息、非自动回复）后触发，供宿主接"隔离 Agent 自动处理"。
    /// 触发为 fire-and-forget，不阻塞对端 HTTP 响应。
    /// </summary>
    public event Action<PeerMessageReceivedArgs>? PeerMessageReceived;

    public void RaisePeerMessageReceived(PeerMessageReceivedArgs args) => PeerMessageReceived?.Invoke(args);

    /// <summary>
    /// 群组事件被提交/落库后触发（星型拓扑 §12.3：协调者据此驱动群自动交流等本机联动）。
    /// 参数：groupId、本次落库的事件。触发为 fire-and-forget，不阻塞对端 HTTP 响应。
    /// </summary>
    public event Action<string, IReadOnlyList<SessionEvent>>? GroupEventsIngested;

    public void RaiseGroupEventsIngested(string groupId, IReadOnlyList<SessionEvent> events) =>
        GroupEventsIngested?.Invoke(groupId, events);

    public Func<PeerProfile>? ProfileProvider { get; set; }
    public int ActualPort { get; private set; }
    public Uri Endpoint { get; private set; } = new Uri("http://localhost/");

    public A2AHost(A2AHostOptions options, ILanDiscovery? discovery = null)
    {
        _options = options;
        Directory.CreateDirectory(_options.DataDirectory);
        Identity = NodeIdentityService.LoadOrCreateAsync(_options.IdentityDirectory, _options.DisplayName, default, _options.Icon).GetAwaiter().GetResult();
        Store = new A2AStore(_options.DatabasePath);
        Store.InitializeAsync().GetAwaiter().GetResult();
        ArtifactStore = new ArtifactStore(_options.ArtifactDirectory, _options.MaxArtifactBytes, _options.MaxArtifactStoreBytes);
        AgentCard = new AgentCard
        {
            Name = Identity.Identity.DisplayName,
            Description = "AgentBrook A2A node",
            // 版本号用于跨机排查：curl 对端 /.well-known/agent.json 即可确认对方运行的构建。
            Version = "1.0.31-a2a.20261012",
            SupportedInterfaces = new List<AgentInterface>(),
            DefaultInputModes = new List<string> { "text/plain" },
            DefaultOutputModes = new List<string> { "text/plain" },
            Capabilities = new AgentCapabilities { Streaming = false },
            Skills = new List<AgentSkill>()
        };
        Pairing = new PairingService(Identity, Store, this);
        Client = new A2AClientService(Identity, Store);
        Presence = new PresenceService(Identity, Store, Client, _options);
        Discovery = discovery ?? new UdpMulticastLanDiscovery(Identity, _options, this);
        if (Discovery is InMemoryLanDiscovery memory)
        {
            memory.Identity = Identity;
            memory.Host = this;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopCts.Token);
        var ct = linked.Token;

        _app = BuildApplication(_options.ListenPort);
        try
        {
            await _app.StartAsync(ct);
        }
        catch (Exception ex) when (IsBindFailure(ex))
        {
            await _app.DisposeAsync();
            _app = BuildApplication(0);
            await _app.StartAsync(ct);
        }

        RefreshEndpoint();
        UpdateAgentCard();

        await Pairing.InitializeAsync(ct);
        await Discovery.StartAsync(ct);
        await Presence.StartAsync(ct);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _stopCts.CancelAsync();
        if (Discovery is IAsyncDisposable discoveryAsync)
        {
            await discoveryAsync.DisposeAsync();
        }
        Presence.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken);
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await Store.DisposeAsync();
        _stopCts.Dispose();
    }

    private WebApplication BuildApplication(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel();
        builder.WebHost.ConfigureKestrel(options =>
        {
            if (_options.UseHttps && !string.IsNullOrEmpty(_options.HttpsCertificatePath) && File.Exists(_options.HttpsCertificatePath))
            {
                options.ListenAnyIP(port, listenOptions =>
                {
                    listenOptions.UseHttps(_options.HttpsCertificatePath!, _options.HttpsCertificatePassword);
                });
            }
            else if (_options.BindToLoopback)
            {
                if (port == 0) options.Listen(IPAddress.Loopback, 0);
                else options.ListenLocalhost(port);
            }
            else
            {
                options.ListenAnyIP(port);
            }
        });
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(_options);
        builder.Services.AddSingleton(Identity);
        builder.Services.AddSingleton(Store);
        builder.Services.AddSingleton(ArtifactStore);
        builder.Services.AddSingleton(Pairing);
        builder.Services.AddSingleton(Presence);
        builder.Services.AddSingleton(Client);
        builder.Services.AddSingleton(Discovery);
        builder.Services.AddSingleton(AgentCard);
        builder.Services.AddSingleton(this);   // 扩展处理器需要回抛群组事件联动（GroupEventsIngested）
        builder.Services.AddSingleton<AgentBrookA2ARequestHandler>();
        builder.Services.AddSingleton<IA2ARequestHandler>(sp => sp.GetRequiredService<AgentBrookA2ARequestHandler>());
        builder.Services.AddSingleton<AgentBrookExtensionHandler>();
        builder.Services.AddSingleton<A2ASecurityMiddleware>();
        var app = builder.Build();
        if (!string.IsNullOrEmpty(_options.PathBase))
        {
            app.UsePathBase(_options.PathBase);
        }
        app.UseMiddleware<A2ASecurityMiddleware>();
        A2AEndpointMapper.Map(app, this);
        app.Services.GetRequiredService<AgentBrookA2ARequestHandler>().Host = this;
        return app;
    }

    private void RefreshEndpoint()
    {
        if (_app is null)
        {
            return;
        }
        var server = _app.Services.GetRequiredService<IServer>();
        var feature = server.Features.Get<IServerAddressesFeature>();
        var addresses = feature?.Addresses ?? new List<string>();
        var selected = addresses.FirstOrDefault(a => _options.UseHttps ? a.StartsWith("https://", StringComparison.OrdinalIgnoreCase) : a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? addresses.FirstOrDefault()
            ?? "http://localhost";
        // ListenAnyIP 在部分平台只报 [::]；对局域网发现而言 [::1]/[::] 对外不可达，
        // 需要改写为主机的主播局域网 IPv4 地址（无外网时为本机回环）。
        if (selected.Contains("[::]", StringComparison.OrdinalIgnoreCase) || selected.Contains("[::1]", StringComparison.OrdinalIgnoreCase))
        {
            var primary = DetectPrimaryIpv4Address();
            selected = selected.Replace("://[::]:", $"://{primary}:", StringComparison.OrdinalIgnoreCase)
                               .Replace("://[::1]:", $"://{primary}:", StringComparison.OrdinalIgnoreCase);
        }
        selected = selected.Replace("://0.0.0.0:", "://127.0.0.1:", StringComparison.OrdinalIgnoreCase);
        Endpoint = new Uri(selected);
        ActualPort = Endpoint.Port;
    }

    /// <summary>通过出站 UDP connect 探测默认路由对应的本机 IPv4 地址（不实际发包）。</summary>
    private static string DetectPrimaryIpv4Address()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint ep)
            {
                return ep.Address.ToString();
            }
        }
        catch
        {
        }
        return "127.0.0.1";
    }

    private void UpdateAgentCard()
    {
        var baseUrl = Endpoint.ToString().TrimEnd('/') + "/";
        AgentCard.SupportedInterfaces = new List<AgentInterface>
        {
            new AgentInterface
            {
                Url = baseUrl,
                ProtocolBinding = "HTTPJSON",
                ProtocolVersion = "1.0"
            },
            new AgentInterface
            {
                Url = baseUrl + "a2a/json-rpc",
                ProtocolBinding = "JSONRPC",
                ProtocolVersion = "1.0"
            }
        };
    }

    private static bool IsBindFailure(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("Address already in use", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("以一种无法访问", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Unable to bind", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var current = exception;
        while (current is not null)
        {
            if (current is SocketException socketEx && socketEx.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return true;
            }
            current = current.InnerException;
        }
        return false;
    }
}
