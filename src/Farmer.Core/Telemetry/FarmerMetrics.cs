using System.Diagnostics.Metrics;

namespace Farmer.Core.Telemetry;

public static class FarmerMetrics
{
    public const string MeterName = "Farmer";
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    // Run-level counters
    public static readonly Counter<long> RunsStarted =
        Meter.CreateCounter<long>("farmer.runs.started");
    public static readonly Counter<long> RunsCompleted =
        Meter.CreateCounter<long>("farmer.runs.completed");
    public static readonly Counter<long> RunsFailed =
        Meter.CreateCounter<long>("farmer.runs.failed");

    // Stage-level timing
    public static readonly Histogram<double> StageDuration =
        Meter.CreateHistogram<double>("farmer.stage.duration", "ms");

    // VM-side SSH activity
    public static readonly Counter<long> VmCommandsExecuted =
        Meter.CreateCounter<long>("farmer.vm.commands.executed");
    public static readonly Counter<long> VmCommandsFailed =
        Meter.CreateCounter<long>("farmer.vm.commands.failed");

    // --- Phase 6 retrospective metrics ---

    /// <summary>
    /// Counts retrospectives that successfully wrote a <c>qa-retro.md</c> /
    /// <c>review.json</c> pair. Not incremented when the agent's infra failed.
    /// </summary>
    public static readonly Counter<long> QaRetrosWritten =
        Meter.CreateCounter<long>("farmer.qa.retros_written_total");

    /// <summary>
    /// Risk score (0-100) reported by the retrospective agent per run.
    /// Histogram lets Aspire show distribution + percentiles.
    /// </summary>
    public static readonly Histogram<int> QaRiskScore =
        Meter.CreateHistogram<int>("farmer.qa.risk_score");

    /// <summary>
    /// Count of directive suggestions the agent produced per run, tagged by
    /// <c>farmer.qa.scope</c> = {prompts, claude_md, task_packet}.
    /// </summary>
    public static readonly Counter<long> QaDirectiveSuggestions =
        Meter.CreateCounter<long>("farmer.qa.directive_suggestions_total");

    /// <summary>
    /// Increments every time an agent call fails (API error, parse failure,
    /// timeout). Tagged by <c>farmer.qa.failure_reason</c>.
    /// </summary>
    public static readonly Counter<long> QaAgentCallFailures =
        Meter.CreateCounter<long>("farmer.qa.agent_call_failures_total");
}
