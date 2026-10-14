namespace AgentBrook.A2A.Transport.Discovery;

public sealed class DiscoveredEndpoint
{
    public required string NodeId { get; init; }
    public required string Endpoint { get; init; }
    public required string CardPath { get; init; }
    public required string Version { get; init; }
    public DateTimeOffset DiscoveredAt { get; init; } = DateTimeOffset.UtcNow;
}

public interface ILanDiscovery
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<DiscoveredEndpoint> WatchAsync(CancellationToken cancellationToken);
    IReadOnlyCollection<DiscoveredEndpoint> GetDiscovered();
}
