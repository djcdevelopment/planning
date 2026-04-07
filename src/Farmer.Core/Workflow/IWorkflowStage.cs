using Farmer.Core.Models;

namespace Farmer.Core.Workflow;

public enum StageOutcome
{
    Success,
    Failure,
    Skip
}

public sealed class StageResult
{
    public StageOutcome Outcome { get; }
    public string? Error { get; }
    public string StageName { get; }

    private StageResult(StageOutcome outcome, string stageName, string? error = null)
    {
        Outcome = outcome;
        StageName = stageName;
        Error = error;
    }

    public static StageResult Succeeded(string stageName) => new(StageOutcome.Success, stageName);
    public static StageResult Failed(string stageName, string error) => new(StageOutcome.Failure, stageName, error);
    public static StageResult Skipped(string stageName, string reason) => new(StageOutcome.Skip, stageName, reason);
}

public interface IWorkflowStage
{
    string Name { get; }
    RunPhase Phase { get; }
    Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default);
}
