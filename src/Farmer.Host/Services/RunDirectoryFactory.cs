using System.Text.Json;
using Farmer.Core.Config;
using Farmer.Core.Models;
using Microsoft.Extensions.Options;

namespace Farmer.Host.Services;

/// <summary>
/// Reusable component for creating run directories from inbox trigger files.
/// Separated from InboxWatcher so it can be used by manual triggers too.
/// </summary>
public sealed class RunDirectoryFactory
{
    private readonly FarmerSettings _settings;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public RunDirectoryFactory(IOptions<FarmerSettings> settings)
    {
        _settings = settings.Value;
    }

    /// <summary>
    /// Parse a minimal inbox trigger file and create a fully initialized run directory.
    /// Returns the path to the run directory.
    /// </summary>
    /// <param name="priorRunId">When set, the new RunRequest's ParentRunId is populated.
    /// Used by the retry driver to link attempts in a chain.</param>
    /// <param name="priorFeedback">When set, the new RunRequest's Feedback is populated.
    /// LoadPromptsStage will prepend this as a synthetic 0-feedback.md prompt file so
    /// the VM's Claude sees the retry feedback as prompt #0.</param>
    /// <param name="attemptNumber">Attempt index in the retry chain. First attempt is 1.</param>
    public async Task<string> CreateFromInboxFileAsync(
        string inboxFilePath,
        CancellationToken ct = default,
        string? priorRunId = null,
        string? priorFeedback = null,
        int attemptNumber = 1)
    {
        var triggerJson = await File.ReadAllTextAsync(inboxFilePath, ct);
        var trigger = JsonSerializer.Deserialize<InboxTrigger>(triggerJson, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to parse inbox file: {inboxFilePath}");

        var runId = $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var runDir = Path.Combine(_settings.Paths.Runs, runId);

        // Create run directory structure
        Directory.CreateDirectory(runDir);
        Directory.CreateDirectory(Path.Combine(runDir, "logs"));
        Directory.CreateDirectory(Path.Combine(runDir, "artifacts"));

        // Build full request from minimal trigger
        var request = new RunRequest
        {
            RunId = runId,
            TaskId = $"task-{Guid.NewGuid().ToString("N")[..8]}",
            AttemptId = attemptNumber,
            WorkRequestName = trigger.WorkRequestName,
            PromptCount = 0,
            Source = trigger.Source ?? "inbox",
            WorkerMode = trigger.WorkerMode,
            RetryPolicy = trigger.RetryPolicy,
            ParentRunId = priorRunId ?? trigger.ParentRunId,
            Feedback = priorFeedback ?? trigger.Feedback,
        };

        // Write request.json
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var requestPath = Path.Combine(runDir, "request.json");
        await File.WriteAllTextAsync(requestPath, requestJson, ct);

        return runDir;
    }
}

/// <summary>
/// Minimal inbox trigger format. InboxWatcher stamps run_id, task_id, etc.
/// </summary>
public sealed class InboxTrigger
{
    [System.Text.Json.Serialization.JsonPropertyName("work_request_name")]
    public string WorkRequestName { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>Optional worker_mode override passed through to TaskPacket. "real" or "fake". Null = use config default.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("worker_mode")]
    public string? WorkerMode { get; set; }

    /// <summary>Optional opt-in retry policy for this /trigger call. See ADR-011.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("retry_policy")]
    public RetryPolicy? RetryPolicy { get; set; }

    /// <summary>Optional link to a prior run in a manually-chained retry. Usually populated by the driver, not the caller.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("parent_run_id")]
    public string? ParentRunId { get; set; }

    /// <summary>Optional markdown feedback to inject as prompt #0. Usually populated by the retry driver.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("feedback")]
    public string? Feedback { get; set; }
}
