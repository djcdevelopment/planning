namespace Farmer.Messaging;

/// <summary>
/// Configuration for Farmer's NATS integration. Bound from Farmer:Messaging in appsettings.
/// </summary>
public sealed class NatsSettings
{
    public const string SectionName = "Farmer:Messaging";

    /// <summary>NATS URL. Default: nats://127.0.0.1:4222. Set to empty to disable messaging entirely.</summary>
    public string Url { get; set; } = "nats://127.0.0.1:4222";

    /// <summary>When false, DI registers no-op publishers and the event stream is never touched. Useful for unit tests.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>JetStream store directory for the FARMER_RUNS stream + RunArtifacts bucket metadata (server-side).
    /// Informational only — the actual directory is configured in nats.conf on the server side.</summary>
    public string? StoreDirectory { get; set; }
}
