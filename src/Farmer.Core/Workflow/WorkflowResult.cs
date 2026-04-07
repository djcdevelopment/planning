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

    public static WorkflowResult FromState(RunFlowState state, bool success, string? error = null) => new()
    {
        RunId = state.RunId,
        Success = success,
        FinalPhase = state.Phase,
        Error = error ?? state.LastError,
        Attempt = state.Attempt,
        StagesCompleted = new List<string>(state.StagesCompleted),
        StartedAt = state.StartedAt
    };
}
