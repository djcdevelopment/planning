using System.Diagnostics.Metrics;

namespace Farmer.Core.Telemetry;

public static class FarmerMetrics
{
    public const string MeterName = "Farmer";
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> RunsStarted = Meter.CreateCounter<long>("farmer.runs.started");
    public static readonly Counter<long> RunsCompleted = Meter.CreateCounter<long>("farmer.runs.completed");
    public static readonly Counter<long> RunsFailed = Meter.CreateCounter<long>("farmer.runs.failed");
    public static readonly Histogram<double> StageDuration = Meter.CreateHistogram<double>("farmer.stage.duration", "ms");
    public static readonly Counter<long> VmCommandsExecuted = Meter.CreateCounter<long>("farmer.vm.commands.executed");
    public static readonly Counter<long> VmCommandsFailed = Meter.CreateCounter<long>("farmer.vm.commands.failed");
}
