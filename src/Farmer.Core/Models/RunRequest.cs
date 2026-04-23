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

    /// <summary>
    /// Prompts carried inline on the request instead of being read from a
    /// pre-existing sample-plan directory. When populated, LoadPromptsStage uses
    /// these directly and skips the disk scan under
    /// <c>{SamplePlansPath}/{WorkRequestName}/</c>. Intended for clients that
    /// have no shared filesystem with Farmer.Host (e.g. a phone-originated
    /// request over a public tunnel).
    ///
    /// <see cref="WorkRequestName"/> is still used for display / metadata /
    /// retrospective context when inline prompts win. If both this and a disk
    /// directory exist, inline wins.
    /// </summary>
    [JsonPropertyName("prompts_inline")]
    public List<InlinePrompt>? PromptsInline { get; set; }

    /// <summary>
    /// Opaque caller identity attributed to this run. Stamped into
    /// <c>request.json</c> at run-directory creation and surfaced by the
    /// run-browser endpoints so a front-end can filter history per user.
    ///
    /// Populated in one of three ways, in order of precedence:
    /// <list type="number">
    ///   <item><c>user_id</c> on the JSON body (preferred contract).</item>
    ///   <item>The <c>X-Farmer-User-Id</c> HTTP header on the <c>/trigger</c>
    ///         request (convenience for curl; used only when the body field is
    ///         absent).</item>
    ///   <item>Null — back-compat for clients that don't care about identity.</item>
    /// </list>
    ///
    /// Farmer does NOT validate this value (demo posture: the tunnel ingress
    /// is the trust boundary; an authenticating proxy in front of Farmer is
    /// responsible for stamping it). See Phase Demo v2 Stream 3.
    /// </summary>
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }
}
