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
        RemoteProjectPath = "~/projects"
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

        // Should have: 1 mkdir + 2 prompt uploads + 1 task-packet upload = 3 SCP calls
        Assert.Equal(3, ssh.ContentUploads.Count);
        Assert.Contains(ssh.ContentUploads, u => u.RemotePath.Contains("1-Setup.md"));
        Assert.Contains(ssh.ContentUploads, u => u.RemotePath.Contains("2-Build.md"));
        Assert.Contains(ssh.ContentUploads, u => u.RemotePath.Contains("task-packet.json"));
    }

    [Fact]
    public async Task Creates_Directories_OnVm()
    {
        var ssh = new MockSshService();
        var stage = new DeliverStage(ssh, NullLogger<DeliverStage>.Instance);

        var state = new RunFlowState
        {
            RunId = "run-1",
            Vm = MakeVm(),
            TaskPacket = new TaskPacket { RunId = "run-1", Prompts = [new PromptFile { Order = 1, Filename = "1-x.md", Content = "x" }] }
        };

        await stage.ExecuteAsync(state);

        Assert.Single(ssh.Commands);
        Assert.Contains("mkdir -p", ssh.Commands[0].Command);
        Assert.Contains("plans", ssh.Commands[0].Command);
        Assert.Contains(".comms", ssh.Commands[0].Command);
    }

    [Fact]
    public async Task Wipes_plans_and_output_before_mkdir()
    {
        // PR #15's live-verify caught leftover prompts from prior runs polluting the
        // plans/ dir. DeliverStage now rm -rf's plans/ + output/ before recreating
        // them so each run starts from a clean slate.
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

        var prep = ssh.Commands.Single().Command;
        Assert.Contains("rm -rf", prep);
        Assert.Contains("~/projects/plans", prep);
        Assert.Contains("~/projects/output", prep);
        Assert.Contains("mkdir -p", prep);
        // Order matters: rm comes before mkdir so we don't delete what we just created.
        Assert.True(prep.IndexOf("rm -rf", StringComparison.Ordinal)
                  < prep.IndexOf("mkdir -p", StringComparison.Ordinal),
            $"rm -rf must precede mkdir -p in: {prep}");
    }

    [Fact]
    public async Task Does_not_wipe_comms_dir()
    {
        // .comms/progress.md is HeartbeatMiddleware's liveness signal; wiping it
        // could race against worker.sh's first write_progress call. The dir gets
        // mkdir -p'd alongside plans/output, but never rm -rf'd.
        var ssh = new MockSshService();
        var stage = new DeliverStage(ssh, NullLogger<DeliverStage>.Instance);

        var state = new RunFlowState
        {
            RunId = "run-comms",
            Vm = MakeVm(),
            TaskPacket = new TaskPacket
            {
                RunId = "run-comms",
                Prompts = [new PromptFile { Order = 1, Filename = "1-x.md", Content = "x" }],
            },
        };

        await stage.ExecuteAsync(state);

        var prep = ssh.Commands.Single().Command;
        // The rm -rf token list must not include .comms (the mkdir -p list still does).
        var rmSection = prep.Substring(prep.IndexOf("rm -rf", StringComparison.Ordinal),
                                       prep.IndexOf("&&", StringComparison.Ordinal) - prep.IndexOf("rm -rf", StringComparison.Ordinal));
        Assert.DoesNotContain(".comms", rmSection);
        Assert.Contains(".comms", prep);  // still gets mkdir'd
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
            Vm = MakeVm(),
            TaskPacket = new TaskPacket { Prompts = [new PromptFile { Order = 1, Filename = "1-x.md", Content = "x" }] }
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
