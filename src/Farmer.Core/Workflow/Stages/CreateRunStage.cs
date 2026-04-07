using Farmer.Core.Contracts;
using Farmer.Core.Models;

namespace Farmer.Core.Workflow.Stages;

public sealed class CreateRunStage : IWorkflowStage
{
    private readonly IRunStore _runStore;

    public string Name => "CreateRun";
    public RunPhase Phase => RunPhase.Created;

    public CreateRunStage(IRunStore runStore)
    {
        _runStore = runStore;
    }

    public async Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(state.RunId))
        {
            state.RunId = $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        }
        state.TaskId = $"task-{Guid.NewGuid().ToString("N")[..8]}";

        var request = new RunRequest
        {
            RunId = state.RunId,
            TaskId = state.TaskId,
            AttemptId = state.Attempt,
            WorkRequestName = state.WorkRequestName,
            PromptCount = 0,
            Source = "api"
        };

        state.RunRequest = request;
        await _runStore.SaveRunRequestAsync(request, ct);
        await _runStore.SaveRunStatusAsync(state.ToRunStatus(), ct);

        return StageResult.Succeeded(Name);
    }
}
