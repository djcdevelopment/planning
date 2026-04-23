using Farmer.Core.Config;
using Farmer.Core.Layout;
using Xunit;

namespace Farmer.Tests.Tools;

public class RunDirectoryLayoutTests
{
    private readonly VmConfig _vm = new()
    {
        Name = "claudefarm1",
        SshHost = "claudefarm1",
        SshUser = "claude",
        MappedDriveLetter = "N",
        RemoteProjectPath = "~/projects"
    };

    [Fact]
    public void VmPaths_UseForwardSlashes()
    {
        Assert.Equal("~/projects/plans", RunDirectoryLayout.VmPlansDir(_vm));
        Assert.Equal("~/projects/.comms", RunDirectoryLayout.VmCommsDir(_vm));
        Assert.Equal("~/projects/output", RunDirectoryLayout.VmOutputDir(_vm));
    }

    [Fact]
    public void VmProgressFile_CorrectPath()
    {
        Assert.Equal("~/projects/.comms/progress.md", RunDirectoryLayout.VmProgressFile(_vm));
    }

    [Fact]
    public void VmPlanFile_IncludesFilename()
    {
        Assert.Equal("~/projects/plans/1-SetupProject.md", RunDirectoryLayout.VmPlanFile(_vm, "1-SetupProject.md"));
    }

    [Fact]
    public void HostPaths_UseMappedDrive()
    {
        var progressPath = RunDirectoryLayout.HostProgressFile(_vm);
        Assert.StartsWith("N:", progressPath);
        Assert.Contains(".comms", progressPath);
        Assert.Contains("progress.md", progressPath);
    }

    [Fact]
    public void HostOutputPaths_CorrectLayout()
    {
        var manifest = RunDirectoryLayout.HostManifest(_vm);
        var summary = RunDirectoryLayout.HostSummary(_vm);

        Assert.Contains("output", manifest);
        Assert.Contains("manifest.json", manifest);
        Assert.Contains("summary.json", summary);
    }

    [Fact]
    public void RunDirPaths_IncludeRunId()
    {
        var runsPath = @"C:\work\iso\planning-runtime\runs";
        var runId = "run-001";

        Assert.Equal(@"C:\work\iso\planning-runtime\runs\run-001", RunDirectoryLayout.RunDir(runsPath, runId));
        Assert.Contains("request.json", RunDirectoryLayout.RunRequestFile(runsPath, runId));
        Assert.Contains("state.json", RunDirectoryLayout.RunStateFile(runsPath, runId));
        Assert.Contains("events.jsonl", RunDirectoryLayout.RunEventsFile(runsPath, runId));
        Assert.Contains("result.json", RunDirectoryLayout.RunResultFile(runsPath, runId));
        Assert.Contains("task-packet.json", RunDirectoryLayout.RunTaskPacketFile(runsPath, runId));
        Assert.Contains("cost-report.json", RunDirectoryLayout.RunCostReportFile(runsPath, runId));
        Assert.Contains("review.json", RunDirectoryLayout.RunReviewFile(runsPath, runId));
    }

    [Fact]
    public void RunDirPaths_IncludeSubdirs()
    {
        var runsPath = @"C:\work\iso\planning-runtime\runs";
        var runId = "run-001";

        Assert.Contains("logs", RunDirectoryLayout.RunLogsDir(runsPath, runId));
        Assert.Contains("artifacts", RunDirectoryLayout.RunArtifactsDir(runsPath, runId));
    }

    [Fact]
    public void EnsureRunDirectory_CreatesSubdirs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "farmer-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            RunDirectoryLayout.EnsureRunDirectory(tempDir, "run-test");
            Assert.True(Directory.Exists(Path.Combine(tempDir, "run-test")));
            Assert.True(Directory.Exists(Path.Combine(tempDir, "run-test", "logs")));
            Assert.True(Directory.Exists(Path.Combine(tempDir, "run-test", "artifacts")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void VmConfig_MappedDrivePath_Correct()
    {
        Assert.Equal(@"N:\projects", _vm.MappedDrivePath);
    }

    // --- Phase 7.5 Stream F: per-run VM workspaces ---

    private readonly VmConfig _vmAbs = new()
    {
        Name = "vm-golden",
        SshHost = "vm-golden",
        SshUser = "claude",
        RemoteProjectPath = "/home/claude/projects",
        RemoteRunsRoot = "/home/claude/runs",
    };

    [Fact]
    public void VmRunRoot_IncludesRunIdUnderRunsRoot()
    {
        // Run ids carry their own conventional `run-` prefix so the layout
        // uses them verbatim -- no double-prefixing.
        Assert.Equal("/home/claude/runs/run-abc",
            RunDirectoryLayout.VmRunRoot(_vmAbs, "run-abc"));
    }

    [Fact]
    public void VmRunSubdirs_AreNestedUnderRunRoot()
    {
        Assert.Equal("/home/claude/runs/run-x/plans",  RunDirectoryLayout.VmRunPlansDir(_vmAbs, "run-x"));
        Assert.Equal("/home/claude/runs/run-x/output", RunDirectoryLayout.VmRunOutputDir(_vmAbs, "run-x"));
        Assert.Equal("/home/claude/runs/run-x/.comms", RunDirectoryLayout.VmRunCommsDir(_vmAbs, "run-x"));
    }

    [Fact]
    public void VmRunPlanFile_Attaches_Filename()
    {
        Assert.Equal("/home/claude/runs/run-1/plans/1-Build.md",
            RunDirectoryLayout.VmRunPlanFile(_vmAbs, "run-1", "1-Build.md"));
    }

    [Fact]
    public void VmRunTaskPacket_Points_At_Plans_TaskPacketJson()
    {
        Assert.Equal("/home/claude/runs/run-1/plans/task-packet.json",
            RunDirectoryLayout.VmRunTaskPacket(_vmAbs, "run-1"));
    }

    [Fact]
    public void ReaderPathForRunOutput_WalksUp_FromProjectRoot()
    {
        // RemoteProjectPath /home/claude/projects shares /home/claude with
        // RemoteRunsRoot /home/claude/runs -> one `..` + descend into runs.
        // The reader's ResolveRemotePath prefixes RemoteProjectPath so POSIX
        // cat sees /home/claude/projects/../runs/run-1/output/manifest.json.
        var p = RunDirectoryLayout.ReaderPathForRunOutput(_vmAbs, "run-1", "manifest.json");
        Assert.Equal("../runs/run-1/output/manifest.json", p);
    }

    [Fact]
    public void ReaderPathForRunOutput_HandlesNested_ProjectRoot()
    {
        var vm = new VmConfig
        {
            RemoteProjectPath = "/home/claude/projects/sub/dir",
            RemoteRunsRoot = "/home/claude/runs",
        };
        var p = RunDirectoryLayout.ReaderPathForRunOutput(vm, "run-r1", "manifest.json");
        // 4 segments in /home/claude/projects/sub/dir after common prefix of 2 (home, claude)
        // → three `..` then descend into runs/run-r1/output.
        Assert.Equal("../../../runs/run-r1/output/manifest.json", p);
    }

    [Fact]
    public void ReaderPathForRunOutput_Normalizes_BackslashSeparators()
    {
        // A Windows Path.Combine caller might pass "foo\\bar.json"; the reader
        // path is POSIX so backslashes must be collapsed to forward slashes.
        var p = RunDirectoryLayout.ReaderPathForRunOutput(_vmAbs, "run-r1", "sub\\foo.json");
        Assert.Equal("../runs/run-r1/output/sub/foo.json", p);
    }

    [Fact]
    public void ReaderPathForRunOutput_Is_Used_By_SshWorkerFileReader_Resolution()
    {
        // Integration-style assertion: the reader's ResolveRemotePath (via
        // SshWorkerFileReader) takes the string produced here and prefixes
        // RemoteProjectPath, yielding an absolute POSIX path equivalent to
        // VmRunOutputDir + "/manifest.json" after `..` collapses.
        var readerPath = RunDirectoryLayout.ReaderPathForRunOutput(_vmAbs, "run-1", "manifest.json");
        var resolved = $"{_vmAbs.RemoteProjectPath}/{readerPath}";
        // POSIX-resolve the `..` manually for assertion simplicity.
        var segs = new Stack<string>();
        foreach (var s in resolved.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (s == "..") { if (segs.Count > 0) segs.Pop(); }
            else segs.Push(s);
        }
        var collapsed = "/" + string.Join('/', segs.Reverse());
        Assert.Equal("/home/claude/runs/run-1/output/manifest.json", collapsed);
    }

    [Fact]
    public void AllThreeVms_HaveDifferentDriveLetters()
    {
        var vms = new[]
        {
            new VmConfig { Name = "claudefarm1", MappedDriveLetter = "N" },
            new VmConfig { Name = "claudefarm2", MappedDriveLetter = "O" },
            new VmConfig { Name = "claudefarm3", MappedDriveLetter = "P" }
        };

        var paths = vms.Select(v => v.MappedDrivePath).ToHashSet();
        Assert.Equal(3, paths.Count);
    }
}
