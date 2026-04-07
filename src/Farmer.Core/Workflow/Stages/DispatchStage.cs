using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Layout;
using Farmer.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farmer.Core.Workflow.Stages;

public sealed class DispatchStage : IWorkflowStage
{
    private readonly ISshService _ssh;
    private readonly FarmerSettings _settings;
    private readonly ILogger<DispatchStage> _logger;

    public string Name => "Dispatch";
    public RunPhase Phase => RunPhase.Dispatching;

    public DispatchStage(ISshService ssh, IOptions<FarmerSettings> settings, ILogger<DispatchStage> logger)
    {
        _ssh = ssh;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
    {
        if (state.Vm is null)
            return StageResult.Failed(Name, "No VM assigned");

        var vm = state.Vm;
        var timeout = TimeSpan.FromMinutes(_settings.SshDispatchTimeoutMinutes);

        var command = $"cd {RunDirectoryLayout.VmProjectRoot(vm)} && bash worker.sh {state.RunId}";

        _logger.LogInformation("Dispatching to {Vm}: {Command} (timeout: {Timeout}min)",
            vm.Name, command, _settings.SshDispatchTimeoutMinutes);

        var result = await _ssh.ExecuteAsync(vm.Name, command, timeout, ct);

        if (!result.Success)
        {
            _logger.LogError("Dispatch failed on {Vm}: exit={Exit} stderr={StdErr}",
                vm.Name, result.ExitCode, result.StdErr);
            return StageResult.Failed(Name,
                $"SSH dispatch failed (exit {result.ExitCode}): {result.StdErr}");
        }

        _logger.LogInformation("Dispatch completed on {Vm}: exit={Exit}", vm.Name, result.ExitCode);
        return StageResult.Succeeded(Name);
    }
}
