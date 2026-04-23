using System.Text.Json.Serialization;

namespace Farmer.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunPhase
{
    Created,
    Loading,
    Reserving,
    Delivering,
    Dispatching,
    Executing,
    Collecting,
    Archiving,
    Reviewing,
    Finalizing,
    Complete,
    Failed,
    Retrying
}

public sealed class RunStatus
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("phase")]
    public RunPhase Phase { get; set; } = RunPhase.Created;

    [JsonPropertyName("progress_pct")]
    public int ProgressPct { get; set; }

    [JsonPropertyName("vm_id")]
    public string? VmId { get; set; }

    [JsonPropertyName("attempt")]
    public int Attempt { get; set; } = 1;

    [JsonPropertyName("current_prompt")]
    public int? CurrentPrompt { get; set; }

    [JsonPropertyName("total_prompts")]
    public int? TotalPrompts { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("stages_completed")]
    public List<string> StagesCompleted { get; set; } = [];
}
