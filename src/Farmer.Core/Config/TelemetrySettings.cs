namespace Farmer.Core.Config;

public sealed class TelemetrySettings
{
    public string ServiceName { get; set; } = "Farmer";
    public string OtlpEndpoint { get; set; } = "http://localhost:18889";
    public bool EnableConsoleExporter { get; set; } = true;
    public bool EnableOtlpExporter { get; set; } = true;
}
