using System.Diagnostics;
using Farmer.Core.Models;
using Farmer.Core.Telemetry;
using Farmer.Core.Workflow;

namespace Farmer.Core.Middleware;

public sealed class TelemetryMiddleware : IWorkflowMiddleware
{
    public async Task<StageResult> InvokeAsync(
        IWorkflowStage stage,
        RunFlowState state,
        Func<Task<StageResult>> next,
        CancellationToken ct = default)
    {
        using var activity = FarmerDiagnostics.Source.StartActivity($"stage.{stage.Name}");
        activity?.SetTag("farmer.run_id", state.RunId);
        activity?.SetTag("farmer.stage_name", stage.Name);
        activity?.SetTag("farmer.stage_phase", stage.Phase.ToString());

        var sw = Stopwatch.StartNew();
        var result = await next();
        sw.Stop();

        activity?.SetTag("farmer.stage_outcome", result.Outcome.ToString());
        activity?.SetTag("farmer.stage_duration_ms", sw.Elapsed.TotalMilliseconds);

        if (result.Outcome == StageOutcome.Failure)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Error);
        }

        return result;
    }
}
