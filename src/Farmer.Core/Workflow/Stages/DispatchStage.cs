using Farmer.Core.Models;

namespace Farmer.Core.Workflow.Stages;

/// <summary>
/// Stub: SSH triggers worker.sh on VM.
/// Real implementation wired in Phase 3.
/// </summary>
public sealed class DispatchStage : IWorkflowStage
{
    public string Name => "Dispatch";
    public RunPhase Phase => RunPhase.Dispatching;

    public Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
    {
        // Stub — real implementation will call ISshService.ExecuteAsync()
        return Task.FromResult(StageResult.Succeeded(Name));
    }
}
