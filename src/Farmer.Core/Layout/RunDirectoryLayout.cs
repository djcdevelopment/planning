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

    // --- Host-side paths (mapped drive, READ-ONLY) ---

    public static string HostProgressFile(VmConfig vm) => Path.Combine(vm.MappedDrivePath, ".comms", "progress.md");
    public static string HostPlansDir(VmConfig vm) => Path.Combine(vm.MappedDrivePath, "plans");
    public static string HostOutputDir(VmConfig vm) => Path.Combine(vm.MappedDrivePath, "output");
    public static string HostManifest(VmConfig vm) => Path.Combine(vm.MappedDrivePath, "output", "manifest.json");
    public static string HostSummary(VmConfig vm) => Path.Combine(vm.MappedDrivePath, "output", "summary.json");
    public static string HostExecutionLog(VmConfig vm) => Path.Combine(vm.MappedDrivePath, "output", "execution-log.txt");

    // --- Run store paths (host-local, for archival) ---

    public static string RunDir(string runStorePath, string runId) => Path.Combine(runStorePath, runId);
    public static string RunRequestFile(string runStorePath, string runId) => Path.Combine(runStorePath, runId, "request.json");
    public static string RunTaskPacketFile(string runStorePath, string runId) => Path.Combine(runStorePath, runId, "task-packet.json");
    public static string RunStatusFile(string runStorePath, string runId) => Path.Combine(runStorePath, runId, "status.json");
    public static string RunCostReportFile(string runStorePath, string runId) => Path.Combine(runStorePath, runId, "cost-report.json");
    public static string RunReviewFile(string runStorePath, string runId) => Path.Combine(runStorePath, runId, "review.json");
    public static string RunFinalStatusFile(string runStorePath, string runId) => Path.Combine(runStorePath, runId, "final-status.json");

    public static void EnsureRunDirectory(string runStorePath, string runId)
    {
        Directory.CreateDirectory(RunDir(runStorePath, runId));
    }
}
