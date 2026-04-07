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
        RemoteProjectPath = "~/projects"
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
        Assert.Contains("worker.sh", ssh.Commands[0].Command);
        Assert.Contains("run-1", ssh.Commands[0].Command);
        Assert.Contains("~/projects", ssh.Commands[0].Command);
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
