using Farmer.Core.Models;

namespace Farmer.Core.Workflow;

public sealed class WorkflowResult
{
    public string RunId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public RunPhase FinalPhase { get; set; }
    public string? Error { get; set; }
    public int Attempt { get; set; } = 1;
    public List<string> StagesCompleted { get; set; } = [];
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
    public double DurationSeconds => (CompletedAt - StartedAt).TotalSeconds;

    /// <summary>
    /// Review verdict from RetrospectiveStage, if the pipeline reached it and the
    /// retrospective agent returned a verdict. Null when the retrospective stage
    /// didn't run (earlier failure) or the agent AutoPassed (no OpenAI key). The
    /// retry driver uses this to decide whether to loop again.
    /// </summary>
    public ReviewVerdict? ReviewVerdict { get; set; }

    public static WorkflowResult FromState(RunFlowState state, bool success, string? error = null) => new()
    {
        RunId = state.RunId,
        Success = success,
        FinalPhase = state.Phase,
        Error = error ?? state.LastError,
        Attempt = state.Attempt,
        StagesCompleted = new List<string>(state.StagesCompleted),
        StartedAt = state.StartedAt,
        ReviewVerdict = state.ReviewVerdict,
    };
}
