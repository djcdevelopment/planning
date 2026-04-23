using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Farmer.Core.Workflow.Stages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farmer.Tests.Workflow;

public class DispatchStageTests
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

    private static DispatchStage MakeStage(MockSshService ssh, int timeoutMin = 30)
    {
        var settings = Options.Create(new FarmerSettings { SshDispatchTimeoutMinutes = timeoutMin });
        return new DispatchStage(ssh, settings, NullLogger<DispatchStage>.Instance);
    }

    [Fact]
    public async Task Dispatches_WorkerSh_ViaSSH()
    {
        var ssh = new MockSshService();
        var stage = MakeStage(ssh);

        var state = new RunFlowState { RunId = "run-1", Vm = MakeVm() };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.Single(ssh.Commands);
        var cmd = ssh.Commands[0].Command;
        Assert.Contains("worker.sh", cmd);
        Assert.Contains("run-1", cmd);
        Assert.Contains("/home/claude/projects", cmd);
    }

    [Fact]
    public async Task Exports_WorkDir_To_PerRun_Workspace()
    {
        // Phase 7.5 Stream F: worker.sh receives WORK_DIR as the bash env
        // var pointing at {RemoteRunsRoot}/run-{run_id}. That's the only
        // channel the host has for telling the script which workspace to use.
        var ssh = new MockSshService();
        var stage = MakeStage(ssh);

        var state = new RunFlowState { RunId = "run-abc123", Vm = MakeVm() };

        await stage.ExecuteAsync(state);

        var cmd = ssh.Commands[0].Command;
        Assert.Contains("WORK_DIR=/home/claude/runs/run-abc123", cmd);
        // WORK_DIR must be a prefix of the `bash worker.sh` invocation --
        // bash applies a single-command env override only when the assignment
        // directly precedes the command.
        var workDirIdx = cmd.IndexOf("WORK_DIR=", StringComparison.Ordinal);
        var bashIdx = cmd.IndexOf("bash worker.sh", StringComparison.Ordinal);
        Assert.True(workDirIdx >= 0 && bashIdx > workDirIdx,
            $"WORK_DIR= must precede 'bash worker.sh' in: {cmd}");
    }

    [Fact]
    public async Task Cd_Into_ProjectRoot_Where_WorkerSh_Lives()
    {
        // worker.sh lives at {RemoteProjectPath}/worker.sh (vm-golden
        // convention, managed by check-worker-parity.ps1). We still cd
        // there to launch it even though the workspace is elsewhere.
        var ssh = new MockSshService();
        var stage = MakeStage(ssh);

        var state = new RunFlowState { RunId = "run-1", Vm = MakeVm() };

        await stage.ExecuteAsync(state);

        Assert.StartsWith("cd /home/claude/projects", ssh.Commands[0].Command);
    }

    [Fact]
    public async Task UsesConfiguredTimeout()
    {
        var ssh = new MockSshService();
        var stage = MakeStage(ssh, timeoutMin: 45);

        var state = new RunFlowState { RunId = "run-1", Vm = MakeVm() };

        await stage.ExecuteAsync(state);

        Assert.Equal(TimeSpan.FromMinutes(45), ssh.Commands[0].Timeout);
    }

    [Fact]
    public async Task Fails_WhenSshFails()
    {
        var ssh = new MockSshService
        {
            ExecuteResult = new SshResult { ExitCode = 1, StdErr = "command not found" }
        };
        var stage = MakeStage(ssh);

        var state = new RunFlowState { RunId = "run-1", Vm = MakeVm() };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Failure, result.Outcome);
        Assert.Contains("command not found", result.Error);
        Assert.Contains("exit 1", result.Error);
    }

    [Fact]
    public async Task Fails_WhenNoVm()
    {
        var ssh = new MockSshService();
        var stage = MakeStage(ssh);
        var state = new RunFlowState { RunId = "run-1", Vm = null };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Failure, result.Outcome);
    }

    // --- Test double ---

    private sealed class MockSshService : ISshService
    {
        public SshResult ExecuteResult { get; set; } = new() { ExitCode = 0, StdOut = "ok" };
        public List<(string VmName, string Command, TimeSpan? Timeout)> Commands { get; } = [];

        public Task<SshResult> ExecuteAsync(string vmName, string command, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            Commands.Add((vmName, command, timeout));
            return Task.FromResult(ExecuteResult);
        }

        public Task ScpUploadAsync(string vmName, string localPath, string remotePath, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ScpUploadContentAsync(string vmName, string content, string remotePath, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
