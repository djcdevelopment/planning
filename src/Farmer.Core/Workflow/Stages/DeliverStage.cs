using Farmer.Core.Models;

namespace Farmer.Core.Workflow.Stages;

/// <summary>
/// Stub: SCP uploads plan files + CLAUDE.md to VM.
/// Real implementation wired in Phase 3.
/// </summary>
public sealed class DeliverStage : IWorkflowStage
{
    public string Name => "Deliver";
    public RunPhase Phase => RunPhase.Delivering;

    public Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
    {
        // Stub — real implementation will call ISshService.ScpUploadAsync()
        return Task.FromResult(StageResult.Succeeded(Name));
    }
}
