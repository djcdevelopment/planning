using Farmer.Core.Workflow;

namespace Farmer.Core.Middleware;

public interface IWorkflowMiddleware
{
    Task<StageResult> InvokeAsync(
        IWorkflowStage stage,
        RunFlowState state,
        Func<Task<StageResult>> next,
        CancellationToken ct = default);
}
