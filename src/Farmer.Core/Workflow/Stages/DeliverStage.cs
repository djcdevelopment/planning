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

        // Ensure directories exist on VM
        var mkdirResult = await _ssh.ExecuteAsync(vmName,
            $"mkdir -p {RunDirectoryLayout.VmPlansDir(vm)} {RunDirectoryLayout.VmCommsDir(vm)} {RunDirectoryLayout.VmOutputDir(vm)}",
            ct: ct);

        if (!mkdirResult.Success)
            return StageResult.Failed(Name, $"Failed to create directories on VM: {mkdirResult.StdErr}");

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
