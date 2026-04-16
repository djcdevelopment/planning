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

    /// <summary>
    /// Set by RetrospectiveStage after the QA agent call. Null if the
    /// pipeline didn't reach the retrospective stage or the agent failed.
    /// </summary>
    public ReviewVerdict? ReviewVerdict { get; set; }

    /// <summary>
    /// Structured directive suggestions from the retrospective agent. Distinct from
    /// <see cref="ReviewVerdict.Suggestions"/> (flat strings) -- each item carries
    /// a scope (Prompts / ClaudeMd / TaskPacket), target, rationale, and suggested
    /// value. Threaded to FeedbackBuilder so retry prompts can cite specific files
    /// and changes rather than just general guidance. Empty when the retrospective
    /// didn't run or the agent produced none.
    /// </summary>
    public IReadOnlyList<DirectiveSuggestion> DirectiveSuggestions { get; set; } = Array.Empty<DirectiveSuggestion>();

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
