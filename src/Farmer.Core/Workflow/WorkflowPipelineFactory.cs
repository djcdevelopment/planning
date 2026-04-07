using Farmer.Core.Middleware;
using Farmer.Core.Workflow.Stages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farmer.Core.Workflow;

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
            _sp.GetRequiredService<ReviewStage>(),
        };

        var costTracker = new CostTrackingMiddleware();

        var middleware = new IWorkflowMiddleware[]
        {
            _sp.GetRequiredService<LoggingMiddleware>(),
            _sp.GetRequiredService<TelemetryMiddleware>(),
            costTracker,
            _sp.GetRequiredService<HeartbeatMiddleware>(),
        };

        var logger = _sp.GetRequiredService<ILogger<RunWorkflow>>();
        var workflow = new RunWorkflow(stages, logger, middleware);

        return (workflow, costTracker);
    }
}
