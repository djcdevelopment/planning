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
}
