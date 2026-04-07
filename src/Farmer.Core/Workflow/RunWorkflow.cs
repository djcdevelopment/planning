using Farmer.Core.Models;
using Microsoft.Extensions.Logging;

namespace Farmer.Core.Workflow;

public sealed class RunWorkflow
{
    private readonly IReadOnlyList<IWorkflowStage> _stages;
    private readonly ILogger<RunWorkflow> _logger;

    public RunWorkflow(IEnumerable<IWorkflowStage> stages, ILogger<RunWorkflow> logger)
    {
        _stages = stages.ToList();
        _logger = logger;
    }

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
                result = await stage.ExecuteAsync(state, ct);
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
}
