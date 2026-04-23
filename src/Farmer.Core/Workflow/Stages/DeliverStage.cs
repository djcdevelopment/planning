using System.Text.Json;
using Farmer.Core.Contracts;
using Farmer.Core.Layout;
using Farmer.Core.Models;
using Microsoft.Extensions.Logging;

namespace Farmer.Core.Workflow.Stages;

public sealed class DeliverStage : IWorkflowStage
{
    private readonly ISshService _ssh;
    private readonly ILogger<DeliverStage> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public string Name => "Deliver";
    public RunPhase Phase => RunPhase.Delivering;

    public DeliverStage(ISshService ssh, ILogger<DeliverStage> logger)
    {
        _ssh = ssh;
        _logger = logger;
    }

    public async Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
    {
        if (state.Vm is null)
            return StageResult.Failed(Name, "No VM assigned — ReserveVmStage must run first");

        if (state.TaskPacket is null)
            return StageResult.Failed(Name, "No TaskPacket — LoadPromptsStage must run first");

        var vm = state.Vm;
        var vmName = vm.Name;
        var runId = state.RunId;

        // Phase 7.5 Stream F: each run gets its own workspace at
        // {RemoteRunsRoot}/run-{run_id}/ so runs whose project-name convention
        // would otherwise collide on the shared {RemoteProjectPath} can't
        // contaminate each other. No rm -rf is needed here because the dir
        // name is freshly derived from run_id and couldn't exist.
        var runRoot = RunDirectoryLayout.VmRunRoot(vm, runId);
        var plans  = RunDirectoryLayout.VmRunPlansDir(vm, runId);
        var comms  = RunDirectoryLayout.VmRunCommsDir(vm, runId);
        var output = RunDirectoryLayout.VmRunOutputDir(vm, runId);

        _logger.LogInformation("Preparing per-run workspace on {Vm}: {RunRoot}", vmName, runRoot);

        var prepResult = await _ssh.ExecuteAsync(vmName,
            $"mkdir -p {plans} {comms} {output}",
            ct: ct);

        if (!prepResult.Success)
            return StageResult.Failed(Name, $"Failed to prepare directories on VM: {prepResult.StdErr}");

        // Upload each prompt file
        foreach (var prompt in state.TaskPacket.Prompts)
        {
            _logger.LogInformation("Delivering prompt {Order}: {Filename} to {Vm}",
                prompt.Order, prompt.Filename, vmName);

            var remotePath = RunDirectoryLayout.VmRunPlanFile(vm, runId, prompt.Filename);
            await _ssh.ScpUploadContentAsync(vmName, prompt.Content, remotePath, ct);
        }

        // Upload task-packet.json
        var taskPacketJson = JsonSerializer.Serialize(state.TaskPacket, JsonOptions);
        await _ssh.ScpUploadContentAsync(vmName, taskPacketJson,
            RunDirectoryLayout.VmRunTaskPacket(vm, runId), ct);

        _logger.LogInformation("Delivered {Count} prompts + task-packet.json to {Vm}:{RunRoot}",
            state.TaskPacket.Prompts.Count, vmName, runRoot);

        return StageResult.Succeeded(Name);
    }
}
