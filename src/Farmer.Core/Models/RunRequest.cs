using System.Text.Json.Serialization;

namespace Farmer.Core.Models;

public sealed class RunRequest
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("attempt_id")]
    public int AttemptId { get; set; } = 1;

    [JsonPropertyName("work_request_name")]
    public string WorkRequestName { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("prompt_count")]
    public int PromptCount { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "api";
}
