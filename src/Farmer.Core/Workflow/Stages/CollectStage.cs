using Farmer.Core.Models;

namespace Farmer.Core.Workflow.Stages;

/// <summary>
/// Stub: Reads build outputs from mapped drive.
/// Real implementation wired in Phase 3.
/// </summary>
public sealed class CollectStage : IWorkflowStage
{
    public string Name => "Collect";
    public RunPhase Phase => RunPhase.Collecting;

    public Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
    {
        // Stub — real implementation will call IMappedDriveReader.WaitForFileAsync()
        return Task.FromResult(StageResult.Succeeded(Name));
    }
}
