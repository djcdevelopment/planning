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

        // Wipe + recreate plans/ and output/ on the VM so leftover files from prior
        // runs (stale prompts, prior manifest.json, prior per-prompt-timing.jsonl)
        // don't pollute this run. .comms/ is intentionally preserved -- HeartbeatMiddleware
        // reads progress.md from there during execution and worker.sh overwrites the
        // file on its first write_progress call. worker.sh itself lives at
        // ~/projects/worker.sh, sibling to plans/output -- not wiped (parity check
        // from PR #13 handles drift detection).
        var plans  = RunDirectoryLayout.VmPlansDir(vm);
        var comms  = RunDirectoryLayout.VmCommsDir(vm);
        var output = RunDirectoryLayout.VmOutputDir(vm);
        var prepResult = await _ssh.ExecuteAsync(vmName,
            $"rm -rf {plans} {output} && mkdir -p {plans} {comms} {output}",
            ct: ct);

        if (!prepResult.Success)
            return StageResult.Failed(Name, $"Failed to prepare directories on VM: {prepResult.StdErr}");

        // Upload each prompt file
        foreach (var prompt in state.TaskPacket.Prompts)
        {
            _logger.LogInformation("Delivering prompt {Order}: {Filename} to {Vm}",
                prompt.Order, prompt.Filename, vmName);

            var remotePath = RunDirectoryLayout.VmPlanFile(vm, prompt.Filename);
            await _ssh.ScpUploadContentAsync(vmName, prompt.Content, remotePath, ct);
        }

        // Upload task-packet.json
        var taskPacketJson = JsonSerializer.Serialize(state.TaskPacket, JsonOptions);
        await _ssh.ScpUploadContentAsync(vmName, taskPacketJson,
            RunDirectoryLayout.VmTaskPacket(vm), ct);

        _logger.LogInformation("Delivered {Count} prompts + task-packet.json to {Vm}",
            state.TaskPacket.Prompts.Count, vmName);

        return StageResult.Succeeded(Name);
    }
}
