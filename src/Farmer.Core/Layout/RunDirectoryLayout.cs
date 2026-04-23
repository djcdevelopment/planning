using Farmer.Core.Config;

namespace Farmer.Core.Layout;

/// <summary>
/// Knows the canonical paths for all run-related files on both
/// the VM side (used via SSH) and the host side (read via mapped drives).
/// </summary>
public static class RunDirectoryLayout
{
    // --- VM-side paths (used in SSH commands and SCP uploads) ---

    public static string VmProjectRoot(VmConfig vm) => vm.RemoteProjectPath;
    public static string VmPlansDir(VmConfig vm) => $"{vm.RemoteProjectPath}/plans";
    public static string VmCommsDir(VmConfig vm) => $"{vm.RemoteProjectPath}/.comms";
    public static string VmOutputDir(VmConfig vm) => $"{vm.RemoteProjectPath}/output";

    public static string VmProgressFile(VmConfig vm) => $"{vm.RemoteProjectPath}/.comms/progress.md";
    public static string VmClaudeMd(VmConfig vm) => $"{vm.RemoteProjectPath}/CLAUDE.md";
    public static string VmWorkerSh(VmConfig vm) => $"{vm.RemoteProjectPath}/worker.sh";
    public static string VmTaskPacket(VmConfig vm) => $"{vm.RemoteProjectPath}/plans/task-packet.json";

    public static string VmPlanFile(VmConfig vm, string filename) => $"{vm.RemoteProjectPath}/plans/{filename}";

    public static string VmManifest(VmConfig vm) => $"{vm.RemoteProjectPath}/output/manifest.json";
    public static string VmSummary(VmConfig vm) => $"{vm.RemoteProjectPath}/output/summary.json";
    public static string VmExecutionLog(VmConfig vm) => $"{vm.RemoteProjectPath}/output/execution-log.txt";

    // --- VM-side per-run workspace paths (Phase 7.5 Stream F) ---
    //
    // Each run gets its own dir at {RemoteRunsRoot}/run-{run_id}/ so two runs
    // on the same VM whose project conventions collide (e.g. two "discord-bot"
    // work requests) cannot contaminate each other. DeliverStage mkdirs these,
    // DispatchStage points worker.sh at them via WORK_DIR, CollectStage reads
    // from them.

    /// <summary>
    /// Per-run workspace on the VM. The dir name is <c>{run_id}</c> — since
    /// <see cref="RunFlowState.RunId"/> already carries the conventional
    /// <c>run-</c> prefix (e.g. <c>run-20260423-083153-f1a957</c>), we don't
    /// double it. Phase 7.5 Stream F docs describe the shape as
    /// <c>/home/claude/runs/run-&lt;run_id&gt;/</c> assuming the id had no
    /// prefix; production run_ids do, so the end result (e.g.
    /// <c>/home/claude/runs/run-20260423-.../</c>) matches the spec intent.
    /// </summary>
    public static string VmRunRoot(VmConfig vm, string runId) =>
        $"{vm.RemoteRunsRoot.TrimEnd('/')}/{runId}";

    public static string VmRunPlansDir(VmConfig vm, string runId) =>
        $"{VmRunRoot(vm, runId)}/plans";

    public static string VmRunOutputDir(VmConfig vm, string runId) =>
        $"{VmRunRoot(vm, runId)}/output";

    public static string VmRunCommsDir(VmConfig vm, string runId) =>
        $"{VmRunRoot(vm, runId)}/.comms";

    public static string VmRunPlanFile(VmConfig vm, string runId, string filename) =>
        $"{VmRunPlansDir(vm, runId)}/{filename}";

    public static string VmRunTaskPacket(VmConfig vm, string runId) =>
        $"{VmRunPlansDir(vm, runId)}/task-packet.json";

    /// <summary>
    /// Path a caller should hand to <c>IMappedDriveReader</c> (backed by
    /// <c>SshWorkerFileReader</c>) to reach a file inside the per-run output
    /// directory. The reader joins this against <see cref="VmConfig.RemoteProjectPath"/>
    /// (the only base path it understands); we express the run dir as a
    /// parent-relative walk from there so POSIX <c>cat</c> / <c>test -f</c> /
    /// <c>ls -1</c> collapse the <c>..</c> at read time.
    /// <para>
    /// Example: with <c>RemoteProjectPath=/home/claude/projects</c> and
    /// <c>RemoteRunsRoot=/home/claude/runs</c>, a <c>relativeFile</c> of
    /// <c>manifest.json</c> yields <c>../runs/run-&lt;id&gt;/output/manifest.json</c>,
    /// which the reader stitches into
    /// <c>/home/claude/projects/../runs/run-&lt;id&gt;/output/manifest.json</c>
    /// — the kernel resolves the <c>..</c> segment when it opens the file.
    /// </para>
    /// <para>
    /// We cannot pass an absolute path here: the reader's <c>TrimStart('/')</c>
    /// would strip it. The walk is computed dynamically from <c>RemoteProjectPath</c>
    /// and <c>RemoteRunsRoot</c> so non-standard layouts still resolve as long
    /// as both are absolute POSIX paths (see CLAUDE.md "SSH uses absolute
    /// paths" gotcha). Tilde-prefixed <c>RemoteProjectPath</c> is not supported
    /// by the runtime SSH stack — tests that use <c>~/projects</c> exercise
    /// only the in-memory mock reader and never hit this code path.
    /// </para>
    /// </summary>
    public static string ReaderPathForRunOutput(VmConfig vm, string runId, string relativeFile)
        => ReaderPathForRunTarget(vm, runId, VmRunOutputDir(vm, runId), relativeFile);

    /// <summary>
    /// Like <see cref="ReaderPathForRunOutput"/> but targets the per-run workspace ROOT
    /// (e.g. <c>/home/claude/runs/run-&lt;id&gt;/</c>) rather than the output subdir.
    /// Used by ArchiveStage to read project-root-relative paths like <c>sqldiff/cli.py</c>
    /// or <c>pyproject.toml</c> that the worker.sh manifest enumerates from git status.
    /// </summary>
    public static string ReaderPathForRunFile(VmConfig vm, string runId, string relativeFile)
        => ReaderPathForRunTarget(vm, runId, VmRunRoot(vm, runId), relativeFile);

    private static string ReaderPathForRunTarget(VmConfig vm, string runId, string targetDir, string relativeFile)
    {
        var normalized = relativeFile.Replace('\\', '/').TrimStart('/');
        var from = vm.RemoteProjectPath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var to = targetDir.Split('/', StringSplitOptions.RemoveEmptyEntries);

        int common = 0;
        while (common < from.Length && common < to.Length && from[common] == to[common])
            common++;

        var ups = Enumerable.Repeat("..", from.Length - common);
        var downs = to.Skip(common);
        var walk = string.Join('/', ups.Concat(downs));

        return walk.Length == 0 ? normalized : $"{walk}/{normalized}";
    }

    // --- Host-side paths (mapped drive, READ-ONLY) ---

    public static string HostProgressFile(VmConfig vm) => Path.Combine(vm.MappedDrivePath, ".comms", "progress.md");
    public static string HostPlansDir(VmConfig vm) => Path.Combine(vm.MappedDrivePath, "plans");
    public static string HostOutputDir(VmConfig vm) => Path.Combine(vm.MappedDrivePath, "output");
    public static string HostManifest(VmConfig vm) => Path.Combine(vm.MappedDrivePath, "output", "manifest.json");
    public static string HostSummary(VmConfig vm) => Path.Combine(vm.MappedDrivePath, "output", "summary.json");
    public static string HostExecutionLog(VmConfig vm) => Path.Combine(vm.MappedDrivePath, "output", "execution-log.txt");

    // --- Run directory paths (externalized runtime) ---

    public static string RunDir(string runsPath, string runId) => Path.Combine(runsPath, runId);
    public static string RunRequestFile(string runsPath, string runId) => Path.Combine(runsPath, runId, "request.json");
    public static string RunTaskPacketFile(string runsPath, string runId) => Path.Combine(runsPath, runId, "task-packet.json");
    public static string RunStateFile(string runsPath, string runId) => Path.Combine(runsPath, runId, "state.json");
    public static string RunEventsFile(string runsPath, string runId) => Path.Combine(runsPath, runId, "events.jsonl");
    public static string RunResultFile(string runsPath, string runId) => Path.Combine(runsPath, runId, "result.json");
    public static string RunCostReportFile(string runsPath, string runId) => Path.Combine(runsPath, runId, "cost-report.json");
    public static string RunReviewFile(string runsPath, string runId) => Path.Combine(runsPath, runId, "review.json");
    public static string RunLogsDir(string runsPath, string runId) => Path.Combine(runsPath, runId, "logs");
    public static string RunArtifactsDir(string runsPath, string runId) => Path.Combine(runsPath, runId, "artifacts");

    public static void EnsureRunDirectory(string runsPath, string runId)
    {
        var runDir = RunDir(runsPath, runId);
        Directory.CreateDirectory(runDir);
        Directory.CreateDirectory(Path.Combine(runDir, "logs"));
        Directory.CreateDirectory(Path.Combine(runDir, "artifacts"));
    }
}
