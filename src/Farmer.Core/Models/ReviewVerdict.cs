using System.Text.Json.Serialization;

namespace Farmer.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Verdict
{
    Accept,
    Retry,
    Reject
}

public sealed class ReviewVerdict
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("verdict")]
    public Verdict Verdict { get; set; }

    [JsonPropertyName("risk_score")]
    public int RiskScore { get; set; }

    [JsonPropertyName("findings")]
    public List<string> Findings { get; set; } = [];

    [JsonPropertyName("suggestions")]
    public List<string> Suggestions { get; set; } = [];

    [JsonPropertyName("reviewed_at")]
    public DateTimeOffset ReviewedAt { get; set; } = DateTimeOffset.UtcNow;
}
