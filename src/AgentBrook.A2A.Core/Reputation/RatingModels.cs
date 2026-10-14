using System.Text;
using System.Text.Json;

namespace AgentBrook.A2A.Core.Reputation;

public sealed class RatingScores
{
    public int? Cooperation { get; set; }
    public int? Efficiency { get; set; }
    public int? Quality { get; set; }

    public void Validate()
    {
        foreach (var (value, name) in new[] { (Cooperation, nameof(Cooperation)), (Efficiency, nameof(Efficiency)), (Quality, nameof(Quality)) })
        {
            if (value is < 1 or > 5)
            {
                throw new ArgumentOutOfRangeException(name, "Score must be between 1 and 5.");
            }
        }
    }
}

public sealed class RatingCredential
{
    public const string Schema = "io.agentbrook.reputation/v1";

    public required string RatingId { get; init; }
    public required string TaskId { get; init; }
    public required string FromNodeId { get; init; }
    public required string ToNodeId { get; init; }
    public string? DeliveryHash { get; set; }
    public RatingScores Scores { get; set; } = new();
    public string Outcome { get; set; } = "accepted";
    public string? Comment { get; set; }
    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;
    public int Revision { get; init; } = 1;
    public string? Signature { get; set; }

    public byte[] SigningPayload()
    {
        var scores = JsonSerializer.Serialize(Scores);
        return Encoding.UTF8.GetBytes(string.Join("|", RatingId, TaskId, FromNodeId, ToNodeId,
            DeliveryHash ?? "", scores, Outcome, Comment ?? "", IssuedAt.ToUnixTimeMilliseconds(), Revision));
    }

    public string CanonicalJson() => JsonSerializer.Serialize(this);
}

public sealed class ReputationSummary
{
    public int Offered { get; set; }
    public int Accepted { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int Cancelled { get; set; }
    public int Rated { get; set; }
    public double? Quality { get; set; }
    public double? Efficiency { get; set; }
    public double? Cooperation { get; set; }
    public double? OnTimeRate { get; set; }
    public int SourceCount { get; set; }
}
