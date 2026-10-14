using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AgentBrook.A2A.Core.Identity;
using AgentBrook.A2A.Transport.Hosting;

namespace AgentBrook.A2A.Transport.Discovery;

public sealed class InMemoryLanDiscovery : ILanDiscovery
{
    private readonly ConcurrentDictionary<string, DiscoveredEndpoint> _discovered = new();
    private readonly ConcurrentDictionary<string, InMemoryLanDiscovery> _peers = new();
    private readonly Channel<DiscoveredEndpoint> _channel = Channel.CreateUnbounded<DiscoveredEndpoint>();

    public NodeIdentityService? Identity { get; set; }
    public A2AHost? Host { get; set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Identity is null || Host is null)
        {
            return Task.CompletedTask;
        }
        var ep = MakeBeacon();
        foreach (var peer in _peers.Values.Where(p => p.Identity is not null && p.Host is not null))
        {
            peer.Receive(ep);
            Receive(peer.MakeBeacon());
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<DiscoveredEndpoint> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var ep in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return ep;
        }
    }

    public IReadOnlyCollection<DiscoveredEndpoint> GetDiscovered() => _discovered.Values.ToArray();

    public void Connect(InMemoryLanDiscovery peer)
    {
        var key = peer.Identity?.Identity.NodeId ?? Guid.NewGuid().ToString();
        _peers[key] = peer;
    }

    private void Receive(DiscoveredEndpoint ep)
    {
        _discovered[ep.NodeId] = ep;
        _channel.Writer.TryWrite(ep);
    }

    private DiscoveredEndpoint MakeBeacon()
    {
        return new DiscoveredEndpoint
        {
            NodeId = ShortNodeId(Identity!.Identity.NodeId),
            Endpoint = Host!.Endpoint.ToString().TrimEnd('/'),
            CardPath = "/.well-known/agent.json",
            Version = "1.0.0"
        };
    }

    private static string ShortNodeId(string nodeId)
    {
        if (nodeId.Length <= 16)
        {
            return nodeId;
        }
        return nodeId[..8] + ".." + nodeId[^6..];
    }
}
