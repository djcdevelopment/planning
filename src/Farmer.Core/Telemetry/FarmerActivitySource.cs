using System.Diagnostics;

namespace Farmer.Core.Telemetry;

public static class FarmerActivitySource
{
    public const string Name = "Farmer";
    public static readonly ActivitySource Source = new(Name, "1.0.0");

    public static Activity? StartRun(string runId)
    {
        return Source.StartActivity("workflow.run", ActivityKind.Internal)?
            .SetTag("farmer.run_id", runId);
    }

    public static Activity? StartStage(string runId, string stageName)
    {
        return Source.StartActivity($"workflow.stage.{stageName}", ActivityKind.Internal)?
            .SetTag("farmer.run_id", runId)
            .SetTag("farmer.stage", stageName);
    }

    /// <summary>
    /// Span for the end-of-pipeline retrospective. Sits inside the stage span
    /// for Retrospective but is distinct so telemetry consumers can filter by
    /// farmer.qa.* tags without walking parent hierarchy.
    /// </summary>
    public static Activity? StartRetrospective(string runId)
    {
        return Source.StartActivity("workflow.retrospective", ActivityKind.Internal)?
            .SetTag("farmer.run_id", runId);
    }

    /// <summary>
    /// Span around a specific agent invocation. Wraps MAF's own
    /// <c>invoke_agent {name}</c> span as a parent so our code and theirs
    /// show the same run_id correlation in Aspire.
    /// </summary>
    public static Activity? StartAgentReview(string runId, string agentName)
    {
        return Source.StartActivity($"agent.review.{agentName}", ActivityKind.Internal)?
            .SetTag("farmer.run_id", runId)
            .SetTag("farmer.agent_name", agentName);
    }
}
