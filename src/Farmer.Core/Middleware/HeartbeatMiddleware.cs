using Farmer.Core.Contracts;
using Farmer.Core.Layout;
using Farmer.Core.Workflow;
using Microsoft.Extensions.Logging;

namespace Farmer.Core.Middleware;

public sealed class HeartbeatMiddleware : IWorkflowMiddleware
{
    private readonly ISshService _ssh;
    private readonly ILogger<HeartbeatMiddleware> _logger;

    public HeartbeatMiddleware(ISshService ssh, ILogger<HeartbeatMiddleware> logger)
    {
        _ssh = ssh;
        _logger = logger;
    }

    public async Task<StageResult> InvokeAsync(
        IWorkflowStage stage,
        RunFlowState state,
        Func<Task<StageResult>> next,
        CancellationToken ct = default)
    {
        var result = await next();

        // Only write heartbeat if a VM is assigned
        if (state.Vm is null)
            return result;

        try
        {
            var progressContent = BuildProgressContent(state, stage.Name, result);
            var remotePath = RunDirectoryLayout.VmProgressFile(state.Vm);

            await _ssh.ScpUploadContentAsync(state.Vm.Name, progressContent, remotePath, ct);

            _logger.LogDebug("Heartbeat written to {Vm}:{Path}", state.Vm.Name, remotePath);
        }
        catch (Exception ex)
        {
            // Heartbeat failure should not fail the stage
            _logger.LogWarning(ex, "Failed to write heartbeat for stage {Stage} on {Vm}",
                stage.Name, state.Vm.Name);
        }

        return result;
    }

    private static string BuildProgressContent(RunFlowState state, string stageName, StageResult result)
    {
        var totalStages = 7; // known pipeline length
        var completedCount = state.StagesCompleted.Count + (result.Outcome == StageOutcome.Success ? 1 : 0);
        var progressPct = (int)((double)completedCount / totalStages * 100);

        return $"""
            ---
            phase: {state.Phase.ToString().ToLowerInvariant()}
            stage: {stageName}
            outcome: {result.Outcome.ToString().ToLowerInvariant()}
            progress_pct: {progressPct}
            updated: {DateTimeOffset.UtcNow:O}
            stages_completed:
            {string.Join("\n", state.StagesCompleted.Select(s => $"  - {s}"))}
            ---
            Stage '{stageName}' completed with outcome: {result.Outcome}
            """;
    }
}
