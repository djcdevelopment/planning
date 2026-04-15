using System.Text;
using System.Text.Json;
using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Layout;
using Farmer.Core.Models;
using Farmer.Core.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farmer.Core.Workflow.Stages;

/// <summary>
/// Last stage in the pipeline. Calls the retrospective agent to review the
/// worker's output, then writes qa-retro.md + review.json + directive-suggestions.md
/// into the run directory. Per ADR-007, the stage never gates pipeline success:
/// it returns Succeeded unless the agent infrastructure fails AND FailureBehavior==Fail.
/// </summary>
public sealed class RetrospectiveStage : IWorkflowStage
{
    private readonly IRetrospectiveAgent _agent;
    private readonly IMappedDriveReader _driveReader;
    private readonly IRunStore _runStore;
    private readonly RetrospectiveSettings _retroSettings;
    private readonly ILogger<RetrospectiveStage> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public string Name => "Retrospective";
    public RunPhase Phase => RunPhase.Reviewing;

    public RetrospectiveStage(
        IRetrospectiveAgent agent,
        IMappedDriveReader driveReader,
        IRunStore runStore,
        IOptions<RetrospectiveSettings> retroSettings,
        ILogger<RetrospectiveStage> logger)
    {
        _agent = agent;
        _driveReader = driveReader;
        _runStore = runStore;
        _retroSettings = retroSettings.Value;
        _logger = logger;
    }

    public async Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
    {
        if (state.Vm is null)
        {
            _logger.LogWarning("RetrospectiveStage: no VM assigned, skipping retrospective");
            return StageResult.Skipped(Name, "No VM assigned — cannot read worker artifacts");
        }

        using var activity = FarmerActivitySource.StartRetrospective(state.RunId);

        var context = await BuildContextAsync(state, ct);
        var result = await _agent.AnalyzeAsync(context, ct);

        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "Retrospective agent failed for run {RunId}: {Error} (attempts: {Attempts})",
                state.RunId, result.Error, result.AgentCallAttempts);

            FarmerMetrics.QaAgentCallFailures.Add(1,
                new KeyValuePair<string, object?>("farmer.qa.failure_reason", "infra_failure"));

            return _retroSettings.FailureBehavior switch
            {
                RetrospectiveFailureBehavior.AutoPass => StageResult.Succeeded(Name),
                RetrospectiveFailureBehavior.Fail =>
                    StageResult.Failed(Name, $"Retrospective agent failed: {result.Error}"),
                _ => StageResult.Succeeded(Name),
            };
        }

        await WriteArtifactsAsync(state, result, ct);

        // Record on the flow state for the final state.json write
        state.ReviewVerdict = result.Verdict;

        if (result.Verdict is not null)
        {
            FarmerMetrics.QaRetrosWritten.Add(1);
            FarmerMetrics.QaRiskScore.Record(result.Verdict.RiskScore);

            foreach (var suggestion in result.DirectiveSuggestions)
            {
                FarmerMetrics.QaDirectiveSuggestions.Add(1,
                    new KeyValuePair<string, object?>("farmer.qa.scope",
                        suggestion.Scope.ToString().ToLowerInvariant()));
            }
        }

        _logger.LogInformation(
            "Retrospective complete for run {RunId}: verdict={Verdict}, risk={RiskScore}, " +
            "directives={DirectiveCount}, tokens={InTokens}/{OutTokens}",
            state.RunId, result.Verdict?.Verdict, result.Verdict?.RiskScore,
            result.DirectiveSuggestions.Count, result.InputTokens, result.OutputTokens);

        return StageResult.Succeeded(Name);
    }

    private async Task<RetrospectiveContext> BuildContextAsync(RunFlowState state, CancellationToken ct)
    {
        var vm = state.Vm!;
        var manifestJson = await SafeReadAsync(vm.Name, "output/manifest.json", ct);
        var summaryJson = await SafeReadAsync(vm.Name, "output/summary.json", ct);
        var workerRetro = await SafeReadAsync(vm.Name, "output/worker-retro.md", ct);
        var execLog = await SafeReadTailAsync(vm.Name, "output/execution-log.txt",
            _retroSettings.ExecutionLogTailLines, ct);

        return new RetrospectiveContext
        {
            RunId = state.RunId,
            Attempt = state.Attempt,
            Request = state.RunRequest ?? new RunRequest(),
            TaskPacket = state.TaskPacket ?? new TaskPacket(),
            ManifestJson = manifestJson ?? "{}",
            SummaryJson = summaryJson,
            WorkerRetroMarkdown = workerRetro,
            ExecutionLogTail = execLog,
            SampledOutputs = Array.Empty<ArtifactSnippet>(),
        };
    }

    private async Task WriteArtifactsAsync(
        RunFlowState state, RetrospectiveResult result, CancellationToken ct)
    {
        var runDir = state.RunDirectory;
        if (runDir is null) return;

        if (result.Verdict is not null)
        {
            result.Verdict.RunId = state.RunId;
            var reviewPath = Path.Combine(runDir, "review.json");
            await WriteAtomicAsync(reviewPath,
                JsonSerializer.Serialize(result.Verdict, JsonOpts), ct);
            await _runStore.SaveReviewVerdictAsync(result.Verdict, ct);
        }

        if (!string.IsNullOrWhiteSpace(result.QaRetroMarkdown))
        {
            await WriteAtomicAsync(
                Path.Combine(runDir, "qa-retro.md"), result.QaRetroMarkdown, ct);
        }

        if (result.DirectiveSuggestions.Count > 0)
        {
            var md = BuildDirectiveSuggestionsMarkdown(
                state.RunId, result.DirectiveSuggestions);
            await WriteAtomicAsync(
                Path.Combine(runDir, "directive-suggestions.md"), md, ct);
        }
    }

    private static string BuildDirectiveSuggestionsMarkdown(
        string runId, IReadOnlyList<DirectiveSuggestion> suggestions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Directive Suggestions for Future Runs");
        sb.AppendLine();
        sb.AppendLine($"Generated from run `{runId}` by the retrospective agent.");
        sb.AppendLine("These suggestions are NOT automatically applied.");
        sb.AppendLine();
        for (var i = 0; i < suggestions.Count; i++)
        {
            var s = suggestions[i];
            sb.AppendLine($"## {i + 1}. [{s.Scope.ToString().ToLowerInvariant()}] {s.Target}");
            sb.AppendLine();
            sb.AppendLine($"**Rationale:** {s.Rationale}");
            if (!string.IsNullOrWhiteSpace(s.CurrentValue))
            {
                sb.AppendLine();
                sb.AppendLine($"**Current:** {s.CurrentValue}");
            }
            sb.AppendLine();
            sb.AppendLine($"**Suggested:** {s.SuggestedValue}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private async Task<string?> SafeReadAsync(
        string vmName, string relativePath, CancellationToken ct)
    {
        try
        {
            if (!await _driveReader.FileExistsAsync(vmName, relativePath, ct))
                return null;
            return await _driveReader.ReadFileAsync(vmName, relativePath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read {Path} from mapped drive for retrospective", relativePath);
            return null;
        }
    }

    private async Task<string?> SafeReadTailAsync(
        string vmName, string relativePath, int tailLines, CancellationToken ct)
    {
        var content = await SafeReadAsync(vmName, relativePath, ct);
        if (content is null) return null;
        var lines = content.Split('\n');
        if (lines.Length <= tailLines) return content;
        return string.Join('\n', lines[^tailLines..]);
    }

    private static async Task WriteAtomicAsync(
        string path, string content, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, path, overwrite: true);
    }
}
