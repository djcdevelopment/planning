using Farmer.Core.Models;

namespace Farmer.Core.Contracts;

/// <summary>
/// Reads a completed run's artifacts and produces a retrospective — a
/// descriptive, forward-looking analysis of what the worker did and what
/// future runs should consider changing. Phase 6 is explicit that this
/// is a post-mortem, not a gate: the return value never fails a workflow.
///
/// The contract lives in Farmer.Core so that <c>RetrospectiveStage</c> can
/// depend on it without importing the Microsoft Agent Framework prerelease
/// surface. The implementation (<c>MafRetrospectiveAgent</c>) lives in
/// Farmer.Agents.
///
/// Implementations MUST NOT throw on Claude API errors, parse failures, or
/// other infrastructure problems. Those outcomes fold into
/// <see cref="RetrospectiveResult.Error"/> and the stage decides what to do
/// via <c>RetrospectiveSettings.FailureBehavior</c>. Implementations throw
/// only on programmer error (null context, invalid config).
/// </summary>
public interface IRetrospectiveAgent
{
    Task<RetrospectiveResult> AnalyzeAsync(
        RetrospectiveContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Everything the retrospective agent needs to review a run. The stage
/// assembles this from the host-local run directory (not from the mapped
/// drive) — <c>CollectStage</c> copies the VM artifacts into the run
/// directory before <c>RetrospectiveStage</c> runs, so the agent sees a
/// stable snapshot.
/// </summary>
public sealed class RetrospectiveContext
{
    public string RunId { get; init; } = string.Empty;
    public int Attempt { get; init; } = 1;
    public RunRequest Request { get; init; } = new();
    public TaskPacket TaskPacket { get; init; } = new();

    /// <summary>Raw contents of <c>manifest.json</c> as written by the worker.</summary>
    public string ManifestJson { get; init; } = string.Empty;

    /// <summary>Raw contents of <c>summary.json</c> if present, else null.</summary>
    public string? SummaryJson { get; init; }

    /// <summary>
    /// Claude's self-review from <c>worker-retro.md</c>. Copied by
    /// CollectStage from the VM's <c>output/worker-retro.md</c>.
    /// </summary>
    public string? WorkerRetroMarkdown { get; init; }

    /// <summary>
    /// Last N lines of <c>execution-log.txt</c> (default 200, configurable
    /// via <c>RetrospectiveSettings.ExecutionLogTailLines</c>).
    /// </summary>
    public string? ExecutionLogTail { get; init; }

    /// <summary>
    /// A sample of the worker's changed files — content truncated per-file
    /// and capped by count to keep the context window under control.
    /// Empty list is fine; the agent just won't have file content to reason
    /// about.
    /// </summary>
    public IReadOnlyList<ArtifactSnippet> SampledOutputs { get; init; } =
        Array.Empty<ArtifactSnippet>();
}

/// <summary>
/// A truncated view of a single output artifact for the retrospective agent's
/// context. See <c>RetrospectiveSettings.MaxFileBytes</c> for the truncation.
/// </summary>
public sealed class ArtifactSnippet
{
    public string Path { get; init; } = string.Empty;

    /// <summary>Content, possibly truncated. UTF-8 text only.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Total file size on disk, even if <see cref="Content"/> is truncated.</summary>
    public long TotalBytes { get; init; }

    /// <summary>True if the content was truncated to fit <c>MaxFileBytes</c>.</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// The retrospective agent's output. See <see cref="IRetrospectiveAgent"/>
/// for failure semantics. A successful result populates <see cref="Verdict"/>,
/// <see cref="QaRetroMarkdown"/>, and <see cref="DirectiveSuggestions"/>.
/// A failed result populates <see cref="Error"/> and leaves the other
/// properties null/empty.
/// </summary>
public sealed class RetrospectiveResult
{
    public ReviewVerdict? Verdict { get; init; }

    /// <summary>The human-readable retrospective markdown, ready to write
    /// to <c>runs/{run_id}/qa-retro.md</c>.</summary>
    public string? QaRetroMarkdown { get; init; }

    public IReadOnlyList<DirectiveSuggestion> DirectiveSuggestions { get; init; } =
        Array.Empty<DirectiveSuggestion>();

    /// <summary>Non-null iff infrastructure failed (API, parse, timeout).</summary>
    public string? Error { get; init; }

    /// <summary>How many Claude calls the agent made (including retries).</summary>
    public int AgentCallAttempts { get; init; }

    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }

    public bool IsSuccess => Verdict is not null && Error is null;
}
