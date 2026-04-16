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

    /// <summary>
    /// Links this run to a prior attempt when the caller explicitly chains runs
    /// (e.g. a human re-drops an inbox file after reading a qa-retro from run N,
    /// wanting run N+1 to know what came before). Phase 6 only records the link;
    /// future phases may use it to aggregate learning across a chain of runs.
    /// </summary>
    [JsonPropertyName("parent_run_id")]
    public string? ParentRunId { get; set; }

    /// <summary>
    /// Optional per-request override for worker execution mode: "real" or "fake".
    /// When null (default), LoadPromptsStage falls back to FarmerSettings.DefaultWorkerMode.
    /// Use "fake" for smoke tests / CI runs that should exercise the pipeline plumbing
    /// without invoking Claude CLI on the VM.
    /// </summary>
    [JsonPropertyName("worker_mode")]
    public string? WorkerMode { get; set; }
}
