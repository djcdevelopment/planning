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
        // Skip ID generation if already set (e.g. from ExecuteFromDirectoryAsync)
        if (string.IsNullOrEmpty(state.RunId))
        {
            state.RunId = $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
            state.TaskId = $"task-{Guid.NewGuid().ToString("N")[..8]}";
        }

        // Respect a pre-populated RunRequest (ExecuteFromDirectoryAsync reads one from
        // request.json and attaches it before the pipeline starts). Overwriting discards
        // per-request fields like Source and WorkerMode that the caller deliberately set.
        if (state.RunRequest is null)
        {
            state.RunRequest = new RunRequest
            {
                RunId = state.RunId,
                TaskId = state.TaskId,
                AttemptId = state.Attempt,
                WorkRequestName = state.WorkRequestName,
                PromptCount = 0,
                Source = "api",
            };
        }

        await _runStore.SaveRunRequestAsync(state.RunRequest, ct);
        await _runStore.SaveRunStateAsync(state.ToRunStatus(), ct);

        return StageResult.Succeeded(Name);
    }
}
