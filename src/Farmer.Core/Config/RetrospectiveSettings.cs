namespace Farmer.Core.Config;

/// <summary>
/// Behavior when the retrospective agent itself cannot reach Claude after
/// <see cref="RetrospectiveSettings.MaxAgentCallRetries"/> attempts.
/// </summary>
public enum RetrospectiveFailureBehavior
{
    /// <summary>
    /// Stage succeeds without writing review.json. The run is complete and
    /// inspectable but has no machine-readable verdict. Appropriate for
    /// Phase 6 "every run is success, data is the product".
    /// </summary>
    AutoPass,

    /// <summary>
    /// Stage fails, workflow fails, result.success = false. Appropriate if
    /// retrospectives are load-bearing for a downstream consumer.
    /// </summary>
    Fail,
}

/// <summary>
/// Config for the Phase 6 retrospective stage — how the retrospective agent
/// is called, sized, and what happens when it fails.
/// </summary>
public sealed class RetrospectiveSettings
{
    public const string SectionName = "Farmer:Retrospective";

    /// <summary>
    /// What to do when Claude is unreachable or returns unparseable output
    /// after all in-stage retries are exhausted. Default
    /// <see cref="RetrospectiveFailureBehavior.AutoPass"/> — a failed
    /// retrospective is a lost learning opportunity, not a failed run.
    /// </summary>
    public RetrospectiveFailureBehavior FailureBehavior { get; set; }
        = RetrospectiveFailureBehavior.AutoPass;

    /// <summary>
    /// Additional Claude calls after the first on parse/API failure. 2 means
    /// up to 3 total calls per retrospective.
    /// </summary>
    public int MaxAgentCallRetries { get; set; } = 2;

    /// <summary>
    /// Maximum number of changed files to sample and pass to the agent as
    /// <c>ArtifactSnippet</c>s. Keeps context window under control when a
    /// worker touches hundreds of files.
    /// </summary>
    public int MaxChangedFiles { get; set; } = 10;

    /// <summary>Per-file byte limit for sampled snippets.</summary>
    public int MaxFileBytes { get; set; } = 8192;

    /// <summary>How many lines from the tail of execution-log.txt to include.</summary>
    public int ExecutionLogTailLines { get; set; } = 200;
}
