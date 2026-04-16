using Farmer.Core.Middleware;
using Farmer.Core.Workflow.Stages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farmer.Core.Workflow;

/// <summary>
/// Builds a fresh RunWorkflow + CostTrackingMiddleware per call. Stateless
/// stages and middleware are resolved as singletons from DI; the
/// CostTrackingMiddleware is constructed inline because it accumulates
/// per-run state and must not be shared across runs.
///
/// This is the answer to the "singleton in denial" problem — we get
/// per-run isolation without scoped DI lifetimes or Reset() shenanigans.
///
/// Adapted from origin/claude/phase5-otel-api by another agent. Two
/// adaptations from their version:
///   1. Includes EventingMiddleware (their branch doesn't have one).
///   2. Middleware ordering is outermost-first per Phase 5's anti-drift
///      contract: Telemetry wraps Logging wraps Eventing wraps Cost wraps
///      Heartbeat. Telemetry must be outermost so the Activity context is
///      alive for every log line emitted by inner middleware.
/// </summary>
public sealed class WorkflowPipelineFactory
{
    private readonly IServiceProvider _sp;

    public WorkflowPipelineFactory(IServiceProvider sp)
    {
        _sp = sp;
    }

    public (RunWorkflow Workflow, CostTrackingMiddleware CostTracker) Create()
    {
        var stages = new IWorkflowStage[]
        {
            _sp.GetRequiredService<CreateRunStage>(),
            _sp.GetRequiredService<LoadPromptsStage>(),
            _sp.GetRequiredService<ReserveVmStage>(),
            _sp.GetRequiredService<DeliverStage>(),
            _sp.GetRequiredService<DispatchStage>(),
            _sp.GetRequiredService<CollectStage>(),
            _sp.GetRequiredService<RetrospectiveStage>(),
        };

        // Fresh per-run instance — no Reset() needed.
        var costTracker = new CostTrackingMiddleware();

        var middleware = new IWorkflowMiddleware[]
        {
            _sp.GetRequiredService<TelemetryMiddleware>(),
            _sp.GetRequiredService<LoggingMiddleware>(),
            _sp.GetRequiredService<EventingMiddleware>(),
            costTracker,
            _sp.GetRequiredService<HeartbeatMiddleware>(),
        };

        var logger = _sp.GetRequiredService<ILogger<RunWorkflow>>();
        var vmManager = _sp.GetRequiredService<Farmer.Core.Contracts.IVmManager>();
        var workflow = new RunWorkflow(stages, logger, middleware, vmManager);

        return (workflow, costTracker);
    }
}
