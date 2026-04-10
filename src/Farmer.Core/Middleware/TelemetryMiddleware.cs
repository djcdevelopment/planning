using System.Diagnostics;
using Farmer.Core.Telemetry;
using Farmer.Core.Workflow;

namespace Farmer.Core.Middleware;

/// <summary>
/// Wraps each stage in an Activity span and records stage duration metrics.
/// Uses the same stage lifecycle boundaries as EventingMiddleware to prevent drift:
/// same stage.Name, same runId, same start/complete/fail/skip transitions.
/// </summary>
public sealed class TelemetryMiddleware : IWorkflowMiddleware
{
    public async Task<StageResult> InvokeAsync(
        IWorkflowStage stage, RunFlowState state,
        Func<Task<StageResult>> next, CancellationToken ct = default)
    {
        using var activity = FarmerActivitySource.StartStage(state.RunId, stage.Name);
        var sw = Stopwatch.StartNew();

        var result = await next();

        sw.Stop();
        FarmerMetrics.StageDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("stage", stage.Name),
            new KeyValuePair<string, object?>("outcome", result.Outcome.ToString()));

        if (result.Outcome == StageOutcome.Failure)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Error);
        }

        return result;
    }
}
