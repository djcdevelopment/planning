using Farmer.Core.Contracts;
using Farmer.Core.Workflow;

namespace Farmer.Host.Services;

/// <summary>
/// Production <see cref="IWorkflowRunner"/>. Wraps <see cref="WorkflowPipelineFactory"/>
/// so each call gets a fresh RunWorkflow + CostTrackingMiddleware pair (factory already
/// guarantees per-run isolation via ADR-004). Saves the cost report to IRunStore after
/// each workflow completes -- a regression from PR #8 where the /trigger inline handler
/// used to do this before delegating to RetryDriver.
/// </summary>
public sealed class PipelineWorkflowRunner : IWorkflowRunner
{
    private readonly WorkflowPipelineFactory _factory;
    private readonly IRunStore _runStore;

    public PipelineWorkflowRunner(WorkflowPipelineFactory factory, IRunStore runStore)
    {
        _factory = factory;
        _runStore = runStore;
    }

    public async Task<WorkflowResult> ExecuteFromDirectoryAsync(string runDir, CancellationToken ct = default)
    {
        var (workflow, costTracker) = _factory.Create();
        var result = await workflow.ExecuteFromDirectoryAsync(runDir, ct);

        // Cost-report persistence -- fix for the regression introduced in PR #8. Pre-PR-#8
        // the /trigger inline handler did: costTracker.GetReport(result.RunId) ->
        // runStore.SaveCostReportAsync(costReport). After PR #8, RetryDriver discarded the
        // costTracker; cost-report.json stopped appearing in run dirs. Restoring it here
        // means every workflow invocation (single-attempt or retry) persists its report.
        try
        {
            var report = costTracker.GetReport(result.RunId);
            await _runStore.SaveCostReportAsync(report, ct);
        }
        catch
        {
            // Cost persistence is best-effort; a failure here should not mask the workflow result.
        }

        return result;
    }
}
