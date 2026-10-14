using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentBrook.A2A.Core.Identity;
using AgentBrook.A2A.Transport.Hosting;

namespace AgentBrook.A2A.Transport.Discovery;

public sealed class UdpMulticastLanDiscovery : ILanDiscovery, IAsyncDisposable
{
    private readonly NodeIdentityService _identity;
    private readonly A2AHostOptions _options;
    private readonly A2AHost _host;
    private readonly IPAddress _multicastAddress;
    private readonly int _port;
    private readonly ConcurrentDictionary<string, DiscoveredEndpoint> _discovered = new();
    private readonly CancellationTokenSource _cts = new();
    private Socket? _socket;
    private Task? _receiveTask;
    private Task? _beaconTask;

    public UdpMulticastLanDiscovery(NodeIdentityService identity, A2AHostOptions options, A2AHost host)
    {
        _identity = identity;
        _options = options;
        _host = host;
        _multicastAddress = IPAddress.Parse("239.255.255.250");
        _port = options.DiscoveryPort;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_socket is not null)
        {
            return Task.CompletedTask;
        }
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Bind(new IPEndPoint(IPAddress.Any, _port));
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(_multicastAddress, IPAddress.Any));
        try
        {
            // Same-host instances must discover each other even when the LAN has no
            // multicast route (VPN, restricted Wi-Fi, missing broadcast support).
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(_multicastAddress, IPAddress.Loopback));
        }
        catch (SocketException)
        {
            // Loopback multicast membership is best-effort; platforms without it still work on a LAN.
        }
        _receiveTask = ReceiveLoopAsync(_cts.Token);
        _beaconTask = BeaconLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync();
        _socket?.Close();
        if (_receiveTask is not null) await _receiveTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ContinueWith(_ => { });
        if (_beaconTask is not null) await _beaconTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ContinueWith(_ => { });
        _socket?.Dispose();
        _socket = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }

    public IReadOnlyCollection<DiscoveredEndpoint> GetDiscovered() => _discovered.Values.ToArray();

    public async IAsyncEnumerable<DiscoveredEndpoint> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 每个发现周期上报全部已知节点（消费端为幂等 upsert）：
        // - 首次发现 → 记录节点
        // - 端点变化（对端重启换端口）→ 刷新端点，杜绝陈旧端口
        // - 每次收到信标 → 刷新 last_seen 存活信号（UI 的"在线"判定依据）
        // 历史教训：曾用"每节点只上报一次/端点变化才上报"两种去重，
        // 前者导致端口变化永不传播、后者导致存活信号冻结被误判离线。
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var ep in _discovered.Values)
            {
                yield return ep;
            }
            await Task.Delay(500, cancellationToken);
        }
    }

    private async Task BeaconLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SendBeaconAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    private async Task SendBeaconAsync(CancellationToken cancellationToken)
    {
        if (_socket is null || _host.Endpoint is null)
        {
            return;
        }
        var port = _host.Endpoint.Port;
        var remote = new IPEndPoint(_multicastAddress, _port);

        // ① 默认接口直发（旧行为，永不回退的保底）：信标端点=默认路由地址。
        //    Windows 上不设置多播出口接口时由 OS 选择，与历史行为一致。
        var primaryIp = DetectPrimaryIpv4Address();
        SendBeaconBytes(remote, primaryIp, port, setInterface: false);

        // ② 多网卡机器可能同时有多个网段（如 192.168.18.x + VPN 192.168.0.x）。
        //    对每个"非默认"接口尽力设置多播出口并广播该接口地址的信标：
        //    对端总能收到与自己同网段、真正可达的地址。
        foreach (var ip in GetLocalIpv4Addresses())
        {
            if (ip.Equals(primaryIp)) continue;
            SendBeaconBytes(remote, ip, port, setInterface: true);
        }

        // ③ 环回信标：同机多实例互发现。多播失败（如 Windows 环回多播受限）时回退单播。
        SendBeaconBytes(remote, IPAddress.Loopback, port, setInterface: true, advertisePrimary: false);
        try
        {
            using var unicast = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var loopbackBeacon = JsonSerializer.SerializeToUtf8Bytes(new
            {
                nodeId = _identity.Identity.NodeId,
                endpoint = _host.Endpoint.ToString().TrimEnd('/'),
                cardPath = "/.well-known/agent.json",
                version = "1.0.0"
            });
            await unicast.SendToAsync(new ArraySegment<byte>(loopbackBeacon), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, _port));
        }
        catch (SocketException)
        {
            // Same-host delivery is best-effort.
        }

        // ④ 子网单播扫描（关键兜底）：发现监听端口（19001）固定，HTTP 端口/IP 怎么变都不影响。
        // 对本机所在每个 /24 网段的全段发单播信标——即使多播被 AP 抑制、对端 DHCP 换了 IP，
        // 只要对方在同一子网运行 Helper，就必然收到并回以信标，双向完成发现与端点刷新。
        try
        {
            var localIps = GetLocalIpv4Addresses();
            var subnets = localIps
                .Select(ip => string.Join('.', ip.GetAddressBytes()[..3]))
                .Distinct()
                .ToList();
            foreach (var subnet in subnets)
            {
                using var sweepSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                var advertiseIp = localIps.FirstOrDefault(x => string.Join('.', x.GetAddressBytes()[..3]) == subnet)
                    ?? localIps.FirstOrDefault();
                if (advertiseIp is null) continue;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    nodeId = _identity.Identity.NodeId,
                    endpoint = $"http://{advertiseIp}:{port}",
                    cardPath = "/.well-known/agent.json",
                    version = "1.0.0"
                });
                for (var last = 1; last <= 254; last++)
                {
                    var target = $"{subnet}.{last}";
                    if (localIps.Any(x => x.ToString() == target)) continue;   // 跳过自己
                    try
                    {
                        sweepSocket.SendTo(bytes, SocketFlags.None, new IPEndPoint(IPAddress.Parse(target), _port));
                    }
                    catch (SocketException)
                    {
                        // 单个地址失败忽略。
                    }
                    if (last % 32 == 0)
                    {
                        await Task.Delay(2, cancellationToken);   // 分片让出，避免突发丢包
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // 子网扫描兜底尽力而为。
        }
    }

    private void SendBeaconBytes(IPEndPoint remote, IPAddress advertiseIp, int port, bool setInterface, bool advertisePrimary = true)
    {
        try
        {
            var endpoint = advertisePrimary
                ? _host.Endpoint.ToString().TrimEnd('/')
                : $"http://{advertiseIp}:{port}";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                nodeId = _identity.Identity.NodeId,
                endpoint,
                cardPath = "/.well-known/agent.json",
                version = "1.0.0"
            });
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (setInterface)
            {
                SetMulticastInterfaceBestEffort(socket, advertiseIp);
            }
            socket.SendTo(bytes, SocketFlags.None, remote);
        }
        catch (SocketException)
        {
            // 单个接口/通道失败不影响其他通道。
        }
    }

    /// <summary>
    /// 尽力而为设置多播出口接口：Unix/macOS 用接口地址字节（已验证）；
    /// Windows 的 IP_MULTICAST_IF 只接受接口索引，主机序/网络序都尝试，
    /// 全部失败则不设置（由 OS 默认接口发出——历史上一直工作的行为）。
    /// </summary>
    private static void SetMulticastInterfaceBestEffort(Socket socket, IPAddress localIp)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var iface = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                        n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(localIp)));
                var ipv4 = iface?.GetIPProperties().GetIPv4Properties();
                if (ipv4 is null) return;
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ipv4.Index);
                    return;
                }
                catch (SocketException) { }
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        IPAddress.HostToNetworkOrder(ipv4.Index));
                    return;
                }
                catch (SocketException) { }
                // 两种字节序都失败：不设置，退回默认接口发送。
            }
            else
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localIp.GetAddressBytes());
            }
        }
        catch (SocketException)
        {
            // 尽力而为：失败则由默认接口发出。
        }
    }

    private static IPAddress DetectPrimaryIpv4Address()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint ep)
            {
                return ep.Address;
            }
        }
        catch
        {
        }
        return IPAddress.Loopback;
    }

    /// <summary>
    /// 设置多播出口接口：Unix/macOS 用接口地址字节；Windows 的 IP_MULTICAST_IF
    /// 只接受接口索引（传地址字节会失败），导致 Windows 端完全发不出局域网信标。
    /// </summary>
    private static void SetMulticastInterface(Socket socket, IPAddress localIp)
    {
        if (OperatingSystem.IsWindows())
        {
            var iface = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                    n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(localIp)));
            var index = iface?.GetIPProperties().GetIPv4Properties()?.Index ?? 0;
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, index);
        }
        else
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localIp.GetAddressBytes());
        }
    }

    /// <summary>本机所有已启用的 IPv4 地址（排除回环），每 30 秒刷新一次缓存。</summary>
    private List<IPAddress> GetLocalIpv4Addresses()
    {
        if (_cachedAddresses.Count > 0 && DateTimeOffset.UtcNow - _addressesCachedAt < TimeSpan.FromSeconds(30))
        {
            return _cachedAddresses;
        }
        try
        {
            _cachedAddresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToList();
        }
        catch
        {
            _cachedAddresses = [];
        }
        _addressesCachedAt = DateTimeOffset.UtcNow;
        return _cachedAddresses;
    }

    private List<IPAddress> _cachedAddresses = [];
    private DateTimeOffset _addressesCachedAt = DateTimeOffset.MinValue;

    private long _receivedCount;

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _socket!.ReceiveFromAsync(new ArraySegment<byte>(buffer), SocketFlags.None, new IPEndPoint(IPAddress.Any, _port));
                _receivedCount++;
                if (_receivedCount % 20 == 1)
                {
                    Console.Error.WriteLine($"[discovery] 已收到 {_receivedCount} 个信标（最新来源 {result.RemoteEndPoint}）");
                }
                var json = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);
                var beacon = JsonSerializer.Deserialize<BeaconMessage>(json);
                if (beacon is null || beacon.nodeId == _identity.Identity.NodeId)
                {
                    continue;
                }
                var ep = new DiscoveredEndpoint
                {
                    NodeId = beacon.nodeId,
                    Endpoint = beacon.endpoint,
                    CardPath = beacon.cardPath,
                    Version = beacon.version
                };
                _discovered[ep.NodeId] = ep;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private static string ShortNodeId(string nodeId)
    {
        if (nodeId.Length <= 16)
        {
            return nodeId;
        }
        return nodeId[..8] + ".." + nodeId[^6..];
    }

    private sealed class BeaconMessage
    {
        public string nodeId { get; set; } = "";
        public string endpoint { get; set; } = "";
        public string cardPath { get; set; } = "";
        public string version { get; set; } = "";
    }
}
