using System.Text.Json;
using Farmer.Core.Middleware;
using Farmer.Core.Models;
using Farmer.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace Farmer.Core.Workflow;

public sealed class RunWorkflow
{
    private readonly IReadOnlyList<IWorkflowStage> _stages;
    private readonly IReadOnlyList<IWorkflowMiddleware> _middleware;
    private readonly ILogger<RunWorkflow> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public RunWorkflow(
        IEnumerable<IWorkflowStage> stages,
        ILogger<RunWorkflow> logger,
        IEnumerable<IWorkflowMiddleware>? middleware = null)
    {
        _stages = stages.ToList();
        _middleware = middleware?.ToList() ?? [];
        _logger = logger;
    }

    /// <summary>
    /// Directory-based entry point. Reads request.json, runs the workflow pipeline,
    /// writes result.json. Used by InboxWatcher and manual triggers.
    /// </summary>
    public async Task<WorkflowResult> ExecuteFromDirectoryAsync(string runDir, CancellationToken ct = default)
    {
        var requestPath = Path.Combine(runDir, "request.json");
        if (!File.Exists(requestPath))
            throw new FileNotFoundException($"request.json not found in {runDir}", requestPath);

        var json = await File.ReadAllTextAsync(requestPath, ct);
        var request = JsonSerializer.Deserialize<RunRequest>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize request.json in {runDir}");

        var state = new RunFlowState
        {
            RunId = request.RunId,
            TaskId = request.TaskId,
            WorkRequestName = request.WorkRequestName,
            Attempt = request.AttemptId,
            RunRequest = request,
            RunDirectory = runDir
        };

        using var activity = FarmerActivitySource.StartRun(state.RunId);
        FarmerMetrics.RunsStarted.Add(1);

        var result = await ExecuteAsync(state, ct);

        if (result.Success)
            FarmerMetrics.RunsCompleted.Add(1);
        else
            FarmerMetrics.RunsFailed.Add(1);

        // Write result.json (atomic)
        var resultPath = Path.Combine(runDir, "result.json");
        var resultJson = JsonSerializer.Serialize(result, JsonOptions);
        var tmpPath = resultPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, resultJson, ct);
        File.Move(tmpPath, resultPath, overwrite: true);

        return result;
    }

    /// <summary>
    /// In-memory entry point. Used by unit tests and direct invocation.
    /// </summary>
    public async Task<WorkflowResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
    {
        _logger.LogInformation("Workflow starting for run {RunId} with {StageCount} stages",
            state.RunId, _stages.Count);

        foreach (var stage in _stages)
        {
            ct.ThrowIfCancellationRequested();

            state.AdvanceTo(stage.Phase);
            _logger.LogInformation("Stage [{StageName}] starting (phase: {Phase})",
                stage.Name, stage.Phase);

            StageResult result;
            try
            {
                result = await ExecuteWithMiddlewareAsync(stage, state, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stage [{StageName}] threw an exception", stage.Name);
                result = StageResult.Failed(stage.Name, ex.Message);
            }

            switch (result.Outcome)
            {
                case StageOutcome.Success:
                    state.RecordStageComplete(stage.Name);
                    _logger.LogInformation("Stage [{StageName}] succeeded", stage.Name);
                    break;

                case StageOutcome.Skip:
                    _logger.LogInformation("Stage [{StageName}] skipped: {Reason}",
                        stage.Name, result.Error);
                    break;

                case StageOutcome.Failure:
                    state.LastError = result.Error;
                    state.AdvanceTo(RunPhase.Failed);
                    _logger.LogError("Stage [{StageName}] failed: {Error}", stage.Name, result.Error);
                    return WorkflowResult.FromState(state, success: false);

                default:
                    throw new InvalidOperationException($"Unknown stage outcome: {result.Outcome}");
            }
        }

        state.AdvanceTo(RunPhase.Complete);
        _logger.LogInformation("Workflow completed for run {RunId}", state.RunId);
        return WorkflowResult.FromState(state, success: true);
    }

    private Task<StageResult> ExecuteWithMiddlewareAsync(
        IWorkflowStage stage, RunFlowState state, CancellationToken ct)
    {
        Func<Task<StageResult>> next = () => stage.ExecuteAsync(state, ct);

        for (var i = _middleware.Count - 1; i >= 0; i--)
        {
            var mw = _middleware[i];
            var currentNext = next;
            next = () => mw.InvokeAsync(stage, state, currentNext, ct);
        }

        return next();
    }
}
