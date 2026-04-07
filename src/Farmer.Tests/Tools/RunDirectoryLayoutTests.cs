using Farmer.Core.Config;
using Farmer.Tools;
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
    public void RunStorePaths_IncludeRunId()
    {
        var runStore = @"D:\work\start\farmer\runs";
        var runId = "run-001";

        Assert.Equal(@"D:\work\start\farmer\runs\run-001", RunDirectoryLayout.RunDir(runStore, runId));
        Assert.Contains("request.json", RunDirectoryLayout.RunRequestFile(runStore, runId));
        Assert.Contains("status.json", RunDirectoryLayout.RunStatusFile(runStore, runId));
        Assert.Contains("task-packet.json", RunDirectoryLayout.RunTaskPacketFile(runStore, runId));
        Assert.Contains("cost-report.json", RunDirectoryLayout.RunCostReportFile(runStore, runId));
        Assert.Contains("review.json", RunDirectoryLayout.RunReviewFile(runStore, runId));
        Assert.Contains("final-status.json", RunDirectoryLayout.RunFinalStatusFile(runStore, runId));
    }

    [Fact]
    public void EnsureRunDirectory_CreatesDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "farmer-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            RunDirectoryLayout.EnsureRunDirectory(tempDir, "run-test");
            Assert.True(Directory.Exists(Path.Combine(tempDir, "run-test")));
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
