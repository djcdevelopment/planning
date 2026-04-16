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

    /// <summary>
    /// Opt-in retry configuration. When null or Enabled=false, /trigger runs one
    /// attempt and returns. When Enabled=true, /trigger loops up to MaxAttempts
    /// and re-runs the workflow with feedback injected from the prior attempt's
    /// ReviewVerdict. Chain links: each retry has ParentRunId pointing at the
    /// previous run. See ADR-011.
    /// </summary>
    [JsonPropertyName("retry_policy")]
    public RetryPolicy? RetryPolicy { get; set; }

    /// <summary>
    /// Markdown feedback from a prior attempt's retrospective, set by the retry
    /// driver before submitting a retry run. Null on a first attempt. Consumed by
    /// LoadPromptsStage, which prepends a synthetic 0-feedback.md prompt file so
    /// Claude on the VM sees the feedback as prompt #0.
    /// </summary>
    [JsonPropertyName("feedback")]
    public string? Feedback { get; set; }
}
