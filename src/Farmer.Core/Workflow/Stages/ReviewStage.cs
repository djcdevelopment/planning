using Farmer.Core.Models;

namespace Farmer.Core.Workflow.Stages;

/// <summary>
/// Stub: Auto-accepts for now.
/// Real QA implementation in Phase 8.
/// </summary>
public sealed class ReviewStage : IWorkflowStage
{
    public string Name => "Review";
    public RunPhase Phase => RunPhase.Reviewing;

    public Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
    {
        // Stub — auto-accept, real QA agent in Phase 8
        return Task.FromResult(StageResult.Succeeded(Name));
    }
}
