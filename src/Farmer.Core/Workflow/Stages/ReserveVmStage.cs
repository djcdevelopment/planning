using Farmer.Core.Contracts;
using Farmer.Core.Models;

namespace Farmer.Core.Workflow.Stages;

public sealed class ReserveVmStage : IWorkflowStage
{
    private readonly IVmManager _vmManager;

    public string Name => "ReserveVm";
    public RunPhase Phase => RunPhase.Reserving;

    public ReserveVmStage(IVmManager vmManager)
    {
        _vmManager = vmManager;
    }

    public async Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
    {
        var vm = await _vmManager.ReserveAsync(ct);

        if (vm is null)
            return StageResult.Failed(Name, "No VMs available in the pool");

        state.Vm = vm;
        return StageResult.Succeeded(Name);
    }
}
