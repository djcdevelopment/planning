namespace Farmer.Core.Config;

public sealed class FarmerSettings
{
    public const string SectionName = "Farmer";

    public List<VmConfig> Vms { get; set; } = [];
    public PathsSettings Paths { get; set; } = new();
    public TelemetrySettings Telemetry { get; set; } = new();
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Path to the SSH private key Renci.SshNet should use. Resolved relative
    /// to the user profile if not absolute. Default is id_ed25519 because
    /// the legacy id_rsa key is encrypted on most dev machines and Renci.SshNet
    /// can't talk to ssh-agent. Set this to an absolute path or a different
    /// filename if your environment uses a different key.
    /// </summary>
    public string SshKeyPath { get; set; } = "id_ed25519";

    public int SshCommandTimeoutSeconds { get; set; } = 30;
    public int SshDispatchTimeoutMinutes { get; set; } = 30;
    public int SshfsCacheLagMs { get; set; } = 500;
    public int ProgressPollIntervalMs { get; set; } = 2000;

    /// <summary>
    /// Worker mode baked into every run's task-packet.json by default. Worker.sh on the
    /// VM reads `.worker_mode` and switches between calling Claude CLI ("real") and
    /// producing canned output ("fake"). Overridable per run by setting `worker_mode`
    /// on the incoming RunRequest. Default "real" keeps production behavior intact;
    /// set to "fake" in dev/CI environments to avoid burning Claude tokens on smoke tests.
    /// </summary>
    public string DefaultWorkerMode { get; set; } = "real";
}
