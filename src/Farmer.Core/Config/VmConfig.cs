namespace Farmer.Core.Config;

public sealed class VmConfig
{
    public string Name { get; set; } = string.Empty;
    public string SshHost { get; set; } = string.Empty;
    public string SshUser { get; set; } = "claude";
    public string MappedDriveLetter { get; set; } = string.Empty;

    /// <summary>
    /// Legacy shared project directory on the VM. Before Phase 7.5 Stream F
    /// this was the single workspace all runs shared -- which collided when two
    /// runs on the same VM happened to produce the same project name (e.g.
    /// Python and JS "discord-bot"). New code should use
    /// <see cref="RemoteRunsRoot"/> + per-run subdirectories; this field is
    /// kept for back-compat (worker.sh lives at <c>{RemoteProjectPath}/worker.sh</c>
    /// on vm-golden, DispatchStage still cd's here to launch the script).
    /// </summary>
    public string RemoteProjectPath { get; set; } = "~/projects";

    /// <summary>
    /// Parent directory on the VM under which each run gets its own workspace
    /// <c>{RemoteRunsRoot}/run-{run_id}/</c> (with <c>plans/</c>, <c>output/</c>,
    /// <c>.comms/</c> subdirs). Added in Phase 7.5 Stream F to isolate runs
    /// that would otherwise collide on project-name convention. Absolute POSIX
    /// path -- Renci.SshNet does not expand <c>~</c>.
    /// </summary>
    public string RemoteRunsRoot { get; set; } = "/home/claude/runs";

    public string MappedDrivePath => $@"{MappedDriveLetter}:\projects";
    public string CommsPath => ".comms";
    public string PlansPath => "plans";
    public string OutputPath => "output";
}
