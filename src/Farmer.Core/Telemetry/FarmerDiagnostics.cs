using System.Diagnostics;

namespace Farmer.Core.Telemetry;

public static class FarmerDiagnostics
{
    public const string ActivitySourceName = "Farmer.Workflow";
    public static readonly ActivitySource Source = new(ActivitySourceName, "1.0.0");
}
