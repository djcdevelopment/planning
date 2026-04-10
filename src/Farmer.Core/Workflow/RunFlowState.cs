using Farmer.Core.Config;
using Farmer.Core.Models;

namespace Farmer.Core.Workflow;

public sealed class RunFlowState
{
    public string RunId { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string WorkRequestName { get; set; } = string.Empty;
    public int Attempt { get; set; } = 1;

    public RunPhase Phase { get; set; } = RunPhase.Created;
    public VmConfig? Vm { get; set; }
    public TaskPacket? TaskPacket { get; set; }
    public RunRequest? RunRequest { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<string> StagesCompleted { get; set; } = [];
    public string? LastError { get; set; }

    /// <summary>
    /// Set by ExecuteFromDirectoryAsync. Used by EventingMiddleware to write
    /// events.jsonl and state.json. Null when running via the in-memory test path.
    /// </summary>
    public string? RunDirectory { get; set; }

    public void AdvanceTo(RunPhase phase)
    {
        Phase = phase;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RecordStageComplete(string stageName)
    {
        StagesCompleted.Add(stageName);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public RunStatus ToRunStatus() => new()
    {
        RunId = RunId,
        Phase = Phase,
        VmId = Vm?.Name,
        Attempt = Attempt,
        TotalPrompts = TaskPacket?.Prompts.Count,
        StartedAt = StartedAt,
        UpdatedAt = UpdatedAt,
        Error = LastError,
        StagesCompleted = new List<string>(StagesCompleted)
    };
}
