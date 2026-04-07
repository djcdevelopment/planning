using System.Collections.Concurrent;
using Farmer.Core.Contracts;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Microsoft.Extensions.Logging;

namespace Farmer.Host.Services;

public sealed class BackgroundWorkflowRunner
{
    private readonly WorkflowPipelineFactory _factory;
    private readonly IRunStore _runStore;
    private readonly ILogger<BackgroundWorkflowRunner> _logger;
    private readonly ConcurrentDictionary<string, Task> _runningTasks = new();

    public BackgroundWorkflowRunner(
        WorkflowPipelineFactory factory,
        IRunStore runStore,
        ILogger<BackgroundWorkflowRunner> logger)
    {
        _factory = factory;
        _runStore = runStore;
        _logger = logger;
    }

    public string StartRun(string workRequestName)
    {
        var runId = $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

        var state = new RunFlowState
        {
            RunId = runId,
            WorkRequestName = workRequestName
        };

        var task = Task.Run(async () =>
        {
            try
            {
                var (workflow, costTracker) = _factory.Create();

                _logger.LogInformation("Workflow {RunId} starting for {WorkRequest}",
                    runId, workRequestName);

                var result = await workflow.ExecuteAsync(state);

                var costReport = costTracker.GetReport(runId);
                await _runStore.SaveCostReportAsync(costReport);
                await _runStore.SaveRunStatusAsync(state.ToRunStatus());

                _logger.LogInformation("Workflow {RunId} completed: {Success}",
                    runId, result.Success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Workflow {RunId} failed with unhandled exception", runId);

                state.LastError = ex.Message;
                state.AdvanceTo(RunPhase.Failed);

                try
                {
                    await _runStore.SaveRunStatusAsync(state.ToRunStatus());
                }
                catch (Exception storeEx)
                {
                    _logger.LogError(storeEx, "Failed to persist error status for {RunId}", runId);
                }
            }
            finally
            {
                _runningTasks.TryRemove(runId, out _);
            }
        });

        _runningTasks.TryAdd(runId, task);
        return runId;
    }

    public bool IsRunning(string runId) => _runningTasks.ContainsKey(runId);
}
