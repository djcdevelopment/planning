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
    public async Task<string> CreateFromInboxFileAsync(string inboxFilePath, CancellationToken ct = default)
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
            AttemptId = 1,
            WorkRequestName = trigger.WorkRequestName,
            PromptCount = 0,
            Source = trigger.Source ?? "inbox"
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
}
