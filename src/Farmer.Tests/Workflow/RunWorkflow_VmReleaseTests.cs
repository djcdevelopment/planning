using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Farmer.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farmer.Tests.Workflow;

/// <summary>
/// Verifies that <see cref="RunWorkflow"/> always releases a reserved VM, regardless
/// of whether the pipeline succeeds, a stage returns Failure, or a stage throws.
/// Companion to the Phase 7 retry driver: without this, multi-attempt /trigger calls
/// fail at attempt 2's ReserveVm stage with "No VMs available in the pool".
/// </summary>
public class RunWorkflow_VmReleaseTests
{
    private static readonly VmConfig TestVm = new() { Name = "spy-vm", SshHost = "spy-vm" };

    [Fact]
    public async Task Released_after_successful_run()
    {
        var vmm = new RecordingVmManager(TestVm);
        var stages = new IWorkflowStage[]
        {
            new LambdaStage("ReserveVm", RunPhase.Reserving, async state =>
            {
                state.Vm = await vmm.ReserveAsync();
                return StageResult.Succeeded("ReserveVm");
            }),
            new LambdaStage("Work", RunPhase.Dispatching, _ =>
                Task.FromResult(StageResult.Succeeded("Work"))),
        };
        var workflow = new RunWorkflow(stages, NullLogger<RunWorkflow>.Instance, vmManager: vmm);

        var result = await workflow.ExecuteAsync(new RunFlowState { RunId = "r1", WorkRequestName = "x" });

        Assert.True(result.Success);
        Assert.Equal(1, vmm.ReleaseCount);
        Assert.Equal("spy-vm", vmm.LastReleased);
        Assert.Equal(VmState.Available, vmm.GetState("spy-vm"));
    }

    [Fact]
    public async Task Released_after_failed_stage()
    {
        var vmm = new RecordingVmManager(TestVm);
        var stages = new IWorkflowStage[]
        {
            new LambdaStage("ReserveVm", RunPhase.Reserving, async state =>
            {
                state.Vm = await vmm.ReserveAsync();
                return StageResult.Succeeded("ReserveVm");
            }),
            new LambdaStage("FailingStage", RunPhase.Dispatching, _ =>
                Task.FromResult(StageResult.Failed("FailingStage", "boom"))),
        };
        var workflow = new RunWorkflow(stages, NullLogger<RunWorkflow>.Instance, vmManager: vmm);

        var result = await workflow.ExecuteAsync(new RunFlowState { RunId = "r2", WorkRequestName = "x" });

        Assert.False(result.Success);
        Assert.Equal(1, vmm.ReleaseCount);
        Assert.Equal(VmState.Available, vmm.GetState("spy-vm"));
    }

    [Fact]
    public async Task Released_after_stage_throws()
    {
        var vmm = new RecordingVmManager(TestVm);
        var stages = new IWorkflowStage[]
        {
            new LambdaStage("ReserveVm", RunPhase.Reserving, async state =>
            {
                state.Vm = await vmm.ReserveAsync();
                return StageResult.Succeeded("ReserveVm");
            }),
            new LambdaStage("ThrowingStage", RunPhase.Dispatching, _ =>
                throw new InvalidOperationException("kaboom")),
        };
        var workflow = new RunWorkflow(stages, NullLogger<RunWorkflow>.Instance, vmManager: vmm);

        var result = await workflow.ExecuteAsync(new RunFlowState { RunId = "r3", WorkRequestName = "x" });

        // RunWorkflow turns stage exceptions into Failure StageResults; result.Success is false.
        Assert.False(result.Success);
        Assert.Equal(1, vmm.ReleaseCount);
        Assert.Equal(VmState.Available, vmm.GetState("spy-vm"));
    }

    [Fact]
    public async Task No_release_when_state_Vm_is_null()
    {
        var vmm = new RecordingVmManager(TestVm);
        var stages = new IWorkflowStage[]
        {
            // No ReserveVm equivalent -- state.Vm stays null.
            new LambdaStage("Work", RunPhase.Created, _ =>
                Task.FromResult(StageResult.Succeeded("Work"))),
        };
        var workflow = new RunWorkflow(stages, NullLogger<RunWorkflow>.Instance, vmManager: vmm);

        var result = await workflow.ExecuteAsync(new RunFlowState { RunId = "r4", WorkRequestName = "x" });

        Assert.True(result.Success);
        Assert.Equal(0, vmm.ReleaseCount);
    }

    [Fact]
    public async Task Skips_release_when_vm_already_in_Error_state()
    {
        // Defensive: if a VM was promoted to Error during the run (e.g. a future
        // stage promoted it via MarkErrorAsync), the finally block must not blindly
        // flip it back to Available. Only Reserved -> Available transition is safe.
        var vmm = new RecordingVmManager(TestVm);
        var stages = new IWorkflowStage[]
        {
            new LambdaStage("ReserveVm", RunPhase.Reserving, async state =>
            {
                state.Vm = await vmm.ReserveAsync();
                return StageResult.Succeeded("ReserveVm");
            }),
            new LambdaStage("ErrorPromote", RunPhase.Dispatching, async state =>
            {
                await vmm.MarkErrorAsync(state.Vm!.Name, "ssh died");
                return StageResult.Succeeded("ErrorPromote");
            }),
        };
        var workflow = new RunWorkflow(stages, NullLogger<RunWorkflow>.Instance, vmManager: vmm);

        await workflow.ExecuteAsync(new RunFlowState { RunId = "r5", WorkRequestName = "x" });

        Assert.Equal(0, vmm.ReleaseCount);
        Assert.Equal(VmState.Error, vmm.GetState("spy-vm"));
    }

    // --- Test double ---

    private sealed class RecordingVmManager : IVmManager
    {
        private readonly Dictionary<string, VmState> _states = new();
        private readonly List<VmConfig> _vms;

        public int ReleaseCount { get; private set; }
        public string? LastReleased { get; private set; }

        public RecordingVmManager(params VmConfig[] vms)
        {
            _vms = vms.ToList();
            foreach (var vm in vms) _states[vm.Name] = VmState.Available;
        }

        public Task<VmConfig?> ReserveAsync(CancellationToken ct = default)
        {
            foreach (var vm in _vms)
            {
                if (_states[vm.Name] == VmState.Available)
                {
                    _states[vm.Name] = VmState.Reserved;
                    return Task.FromResult<VmConfig?>(vm);
                }
            }
            return Task.FromResult<VmConfig?>(null);
        }

        public Task ReleaseAsync(string vmName, CancellationToken ct = default)
        {
            ReleaseCount++;
            LastReleased = vmName;
            _states[vmName] = VmState.Available;
            return Task.CompletedTask;
        }

        public Task MarkBusyAsync(string vmName, CancellationToken ct = default) { _states[vmName] = VmState.Busy; return Task.CompletedTask; }
        public Task MarkErrorAsync(string vmName, string reason, CancellationToken ct = default) { _states[vmName] = VmState.Error; return Task.CompletedTask; }
        public VmState GetState(string vmName) => _states[vmName];
        public IReadOnlyList<VmConfig> GetAllVms() => _vms;
    }
}
