using System.Text.Json;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Farmer.Messaging;
using Microsoft.Extensions.Logging;

namespace Farmer.Host.Services;

/// <summary>
/// Runs one or more workflow attempts per /trigger call, honoring the RetryPolicy
/// on the incoming RunRequest. Feedback from each attempt's ReviewVerdict is
/// injected into the next attempt as a synthetic 0-feedback.md prompt
/// (see <see cref="FeedbackBuilder"/> and <see cref="Farmer.Core.Workflow.Stages.LoadPromptsStage"/>).
///
/// Factored out of Program.cs so integration tests can exercise the loop without
/// bootstrapping the full ASP.NET Core host.
/// </summary>
public sealed class RetryDriver
{
    private readonly RunDirectoryFactory _dirFactory;
    private readonly IWorkflowRunner _runner;
    private readonly IRunArtifactStore _artifactStore;
    private readonly ILogger<RetryDriver> _log;

    private static readonly JsonSerializerOptions RequestReadOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public RetryDriver(
        RunDirectoryFactory dirFactory,
        IWorkflowRunner runner,
        IRunArtifactStore artifactStore,
        ILogger<RetryDriver> log)
    {
        _dirFactory = dirFactory;
        _runner = runner;
        _artifactStore = artifactStore;
        _log = log;
    }

    public async Task<IReadOnlyList<WorkflowResult>> RunAsync(string triggerTempFile, CancellationToken ct = default)
    {
        var attempts = new List<WorkflowResult>();
        string? priorRunId = null;
        string? priorFeedback = null;
        RetryPolicy? policy = null;

        while (true)
        {
            var attemptNumber = attempts.Count + 1;
            var runDir = await _dirFactory.CreateFromInboxFileAsync(
                triggerTempFile, ct, priorRunId, priorFeedback, attemptNumber);

            var result = await _runner.ExecuteFromDirectoryAsync(runDir, ct);
            attempts.Add(result);

            await UploadRunArtifactsAsync(result.RunId, runDir, ct);

            // Read the retry policy out of the first run's request.json.
            // The policy doesn't change between attempts within a single /trigger call.
            if (policy is null)
                policy = await ReadRetryPolicyAsync(runDir, ct);

            var verdict = result.ReviewVerdict?.Verdict;
            var shouldRetry =
                policy is { Enabled: true }
                && attempts.Count < policy.MaxAttempts
                && verdict is not null
                && policy.RetryOnVerdicts.Contains(verdict.ToString() ?? "");

            if (!shouldRetry)
            {
                if (policy is { Enabled: true } && verdict is null)
                    _log.LogInformation("Retry short-circuited for run {RunId}: no ReviewVerdict available (retrospective AutoPassed or failed).", result.RunId);
                break;
            }

            priorRunId = result.RunId;
            priorFeedback = FeedbackBuilder.Render(
                result.ReviewVerdict!,
                priorAttempt: attempts.Count,
                priorRunId: priorRunId,
                directives: result.DirectiveSuggestions);

            _log.LogInformation(
                "Verdict {Verdict} on {RunId}, attempting retry {Attempt}/{Max}",
                verdict, result.RunId, attemptNumber + 1, policy!.MaxAttempts);
        }

        return attempts;
    }

    private async Task UploadRunArtifactsAsync(string runId, string runDir, CancellationToken ct)
    {
        if (!Directory.Exists(runDir)) return;
        foreach (var file in Directory.EnumerateFiles(runDir))
        {
            var name = Path.GetFileName(file);
            try
            {
                var bytes = await File.ReadAllBytesAsync(file, ct);
                await _artifactStore.PutAsync(runId, name, bytes, ct);
            }
            catch { /* store already logs; never abort a run over a NATS hiccup */ }
        }
    }

    private static async Task<RetryPolicy?> ReadRetryPolicyAsync(string runDir, CancellationToken ct)
    {
        var requestPath = Path.Combine(runDir, "request.json");
        if (!File.Exists(requestPath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(requestPath, ct);
            var req = JsonSerializer.Deserialize<RunRequest>(json, RequestReadOpts);
            return req?.RetryPolicy;
        }
        catch { return null; }
    }
}
