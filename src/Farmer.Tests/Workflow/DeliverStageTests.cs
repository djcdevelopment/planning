using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Farmer.Core.Workflow.Stages;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farmer.Tests.Workflow;

public class DeliverStageTests
{
    private static VmConfig MakeVm() => new()
    {
        Name = "claudefarm1",
        SshHost = "claudefarm1",
        SshUser = "claude",
        MappedDriveLetter = "N",
        RemoteProjectPath = "/home/claude/projects",
        RemoteRunsRoot = "/home/claude/runs",
    };

    [Fact]
    public async Task Delivers_AllPromptFiles_ViaScpContent()
    {
        var ssh = new MockSshService();
        var stage = new DeliverStage(ssh, NullLogger<DeliverStage>.Instance);

        var state = new RunFlowState
        {
            RunId = "run-1",
            Vm = MakeVm(),
            TaskPacket = new TaskPacket
            {
                RunId = "run-1",
                WorkRequestName = "my-app",
                Prompts =
                [
                    new PromptFile { Order = 1, Filename = "1-Setup.md", Content = "setup content" },
                    new PromptFile { Order = 2, Filename = "2-Build.md", Content = "build content" }
                ]
            }
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);

        // Should have: 2 prompt uploads + 1 task-packet upload = 3 SCP calls
        Assert.Equal(3, ssh.ContentUploads.Count);
        Assert.Contains(ssh.ContentUploads, u => u.RemotePath.Contains("1-Setup.md"));
        Assert.Contains(ssh.ContentUploads, u => u.RemotePath.Contains("2-Build.md"));
        Assert.Contains(ssh.ContentUploads, u => u.RemotePath.Contains("task-packet.json"));
    }

    [Fact]
    public async Task Uploads_Under_PerRun_Workspace()
    {
        // Phase 7.5 Stream F: destination path is {RemoteRunsRoot}/run-{run_id}/
        // not the legacy shared {RemoteProjectPath}. Two runs on the same VM
        // should land in distinct directories.
        var ssh = new MockSshService();
        var stage = new DeliverStage(ssh, NullLogger<DeliverStage>.Instance);

        var state = new RunFlowState
        {
            RunId = "run-abc123",
            Vm = MakeVm(),
            TaskPacket = new TaskPacket
            {
                RunId = "run-abc123",
                Prompts = [new PromptFile { Order = 1, Filename = "1-Build.md", Content = "x" }],
            },
        };

        await stage.ExecuteAsync(state);

        Assert.All(ssh.ContentUploads, u =>
            Assert.Contains("/home/claude/runs/run-abc123/", u.RemotePath));
        Assert.Contains(ssh.ContentUploads,
            u => u.RemotePath == "/home/claude/runs/run-abc123/plans/1-Build.md");
        Assert.Contains(ssh.ContentUploads,
            u => u.RemotePath == "/home/claude/runs/run-abc123/plans/task-packet.json");
    }

    [Fact]
    public async Task Creates_PerRun_Directories_OnVm()
    {
        var ssh = new MockSshService();
        var stage = new DeliverStage(ssh, NullLogger<DeliverStage>.Instance);

        var state = new RunFlowState
        {
            RunId = "run-xyz",
            Vm = MakeVm(),
            TaskPacket = new TaskPacket { RunId = "run-xyz", Prompts = [new PromptFile { Order = 1, Filename = "1-x.md", Content = "x" }] }
        };

        await stage.ExecuteAsync(state);

        Assert.Single(ssh.Commands);
        var cmd = ssh.Commands[0].Command;
        Assert.Contains("mkdir -p", cmd);
        Assert.Contains("/home/claude/runs/run-xyz/plans", cmd);
        Assert.Contains("/home/claude/runs/run-xyz/.comms", cmd);
        Assert.Contains("/home/claude/runs/run-xyz/output", cmd);
    }

    [Fact]
    public async Task Does_Not_Rm_Rf_Anything()
    {
        // Per-run dir is freshly derived from run_id so it can't pre-exist --
        // no wipe is necessary. Removing the shared-dir rm -rf also removes
        // the risk of blasting a sibling run's workspace.
        var ssh = new MockSshService();
        var stage = new DeliverStage(ssh, NullLogger<DeliverStage>.Instance);

        var state = new RunFlowState
        {
            RunId = "run-clean",
            Vm = MakeVm(),
            TaskPacket = new TaskPacket
            {
                RunId = "run-clean",
                Prompts = [new PromptFile { Order = 1, Filename = "1-x.md", Content = "x" }],
            },
        };

        await stage.ExecuteAsync(state);

        Assert.DoesNotContain("rm -rf", ssh.Commands[0].Command);
    }

    [Fact]
    public async Task Two_Distinct_RunIds_Get_Two_Distinct_Dirs()
    {
        // The collision that motivated Stream F (two discord-bot runs landing
        // in the same project dir) is prevented because the dir name is
        // derived from run_id, which is per-run unique.
        var ssh1 = new MockSshService();
        var ssh2 = new MockSshService();
        var stage1 = new DeliverStage(ssh1, NullLogger<DeliverStage>.Instance);
        var stage2 = new DeliverStage(ssh2, NullLogger<DeliverStage>.Instance);

        var packet = (string rid) => new TaskPacket
        {
            RunId = rid,
            Prompts = [new PromptFile { Order = 1, Filename = "1-Build.md", Content = "x" }],
        };

        await stage1.ExecuteAsync(new RunFlowState { RunId = "run-python", Vm = MakeVm(), TaskPacket = packet("run-python") });
        await stage2.ExecuteAsync(new RunFlowState { RunId = "run-javascript", Vm = MakeVm(), TaskPacket = packet("run-javascript") });

        Assert.Contains("run-python", ssh1.Commands[0].Command);
        Assert.DoesNotContain("run-javascript", ssh1.Commands[0].Command);
        Assert.Contains("run-javascript", ssh2.Commands[0].Command);
        Assert.DoesNotContain("run-python", ssh2.Commands[0].Command);
    }

    [Fact]
    public async Task Fails_WhenNoVm()
    {
        var ssh = new MockSshService();
        var stage = new DeliverStage(ssh, NullLogger<DeliverStage>.Instance);
        var state = new RunFlowState { Vm = null, TaskPacket = new TaskPacket() };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Failure, result.Outcome);
        Assert.Contains("No VM", result.Error);
    }

    [Fact]
    public async Task Fails_WhenNoTaskPacket()
    {
        var ssh = new MockSshService();
        var stage = new DeliverStage(ssh, NullLogger<DeliverStage>.Instance);
        var state = new RunFlowState { Vm = MakeVm(), TaskPacket = null };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Failure, result.Outcome);
        Assert.Contains("TaskPacket", result.Error);
    }

    [Fact]
    public async Task Fails_WhenMkdirFails()
    {
        var ssh = new MockSshService { ExecuteResult = new SshResult { ExitCode = 1, StdErr = "permission denied" } };
        var stage = new DeliverStage(ssh, NullLogger<DeliverStage>.Instance);

        var state = new RunFlowState
        {
            RunId = "run-fail",
            Vm = MakeVm(),
            TaskPacket = new TaskPacket { RunId = "run-fail", Prompts = [new PromptFile { Order = 1, Filename = "1-x.md", Content = "x" }] }
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Failure, result.Outcome);
        Assert.Contains("permission denied", result.Error);
    }

    // --- Test double ---

    private sealed class MockSshService : ISshService
    {
        public SshResult ExecuteResult { get; set; } = new() { ExitCode = 0, StdOut = "ok" };
        public List<(string VmName, string Command)> Commands { get; } = [];
        public List<(string VmName, string RemotePath)> FileUploads { get; } = [];
        public List<(string VmName, string Content, string RemotePath)> ContentUploads { get; } = [];

        public Task<SshResult> ExecuteAsync(string vmName, string command, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            Commands.Add((vmName, command));
            return Task.FromResult(ExecuteResult);
        }

        public Task ScpUploadAsync(string vmName, string localPath, string remotePath, CancellationToken ct = default)
        {
            FileUploads.Add((vmName, remotePath));
            return Task.CompletedTask;
        }

        public Task ScpUploadContentAsync(string vmName, string content, string remotePath, CancellationToken ct = default)
        {
            ContentUploads.Add((vmName, content, remotePath));
            return Task.CompletedTask;
        }
    }
}
