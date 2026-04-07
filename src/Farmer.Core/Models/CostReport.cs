using System.Text.Json.Serialization;

namespace Farmer.Core.Models;

public sealed class CostReport
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("total_duration_seconds")]
    public double TotalDurationSeconds { get; set; }

    [JsonPropertyName("stages")]
    public List<StageCost> Stages { get; set; } = [];

    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StageCost
{
    [JsonPropertyName("stage_name")]
    public string StageName { get; set; } = string.Empty;

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset CompletedAt { get; set; }
}
