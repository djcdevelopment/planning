using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Middleware;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farmer.Tests.Middleware;

public class HeartbeatMiddlewareTests
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
    public async Task WritesProgressViaSSH_WhenVmAssigned()
    {
        var ssh = new MockSshService();
        var mw = new HeartbeatMiddleware(ssh, NullLogger<HeartbeatMiddleware>.Instance);

        var state = new RunFlowState { RunId = "run-1", Vm = MakeVm() };
        var stage = new FakeStage("Deliver", RunPhase.Delivering);

        await mw.InvokeAsync(stage, state, () => Task.FromResult(StageResult.Succeeded("Deliver")));

        Assert.Single(ssh.ContentUploads);
        Assert.Equal("claudefarm1", ssh.ContentUploads[0].VmName);
        Assert.Contains("progress.md", ssh.ContentUploads[0].RemotePath);
        Assert.Contains(".comms", ssh.ContentUploads[0].RemotePath);
    }

    [Fact]
    public async Task ProgressContentContainsStageInfo()
    {
        var ssh = new MockSshService();
        var mw = new HeartbeatMiddleware(ssh, NullLogger<HeartbeatMiddleware>.Instance);

        var state = new RunFlowState { RunId = "run-1", Vm = MakeVm() };
        state.StagesCompleted.Add("CreateRun");
        var stage = new FakeStage("LoadPrompts", RunPhase.Loading);

        await mw.InvokeAsync(stage, state, () => Task.FromResult(StageResult.Succeeded("LoadPrompts")));

        var content = ssh.ContentUploads[0].Content;
        Assert.Contains("LoadPrompts", content);
        Assert.Contains("success", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CreateRun", content); // previously completed stage
    }

    [Fact]
    public async Task SkipsWhenNoVm()
    {
        var ssh = new MockSshService();
        var mw = new HeartbeatMiddleware(ssh, NullLogger<HeartbeatMiddleware>.Instance);

        var state = new RunFlowState { RunId = "run-1", Vm = null };
        var stage = new FakeStage("CreateRun", RunPhase.Created);

        var result = await mw.InvokeAsync(stage, state, () => Task.FromResult(StageResult.Succeeded("CreateRun")));

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.Empty(ssh.ContentUploads); // no SSH calls
    }

    [Fact]
    public async Task DoesNotFailStage_WhenSshFails()
    {
        var ssh = new MockSshService { ShouldThrow = true };
        var mw = new HeartbeatMiddleware(ssh, NullLogger<HeartbeatMiddleware>.Instance);

        var state = new RunFlowState { RunId = "run-1", Vm = MakeVm() };
        var stage = new FakeStage("Deliver", RunPhase.Delivering);

        var result = await mw.InvokeAsync(stage, state, () => Task.FromResult(StageResult.Succeeded("Deliver")));

        // Stage should still succeed even though heartbeat SSH failed
        Assert.Equal(StageOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task CallsNext()
    {
        var ssh = new MockSshService();
        var mw = new HeartbeatMiddleware(ssh, NullLogger<HeartbeatMiddleware>.Instance);

        var state = new RunFlowState { Vm = MakeVm() };
        var called = false;

        await mw.InvokeAsync(new FakeStage("S", RunPhase.Loading), state, () =>
        {
            called = true;
            return Task.FromResult(StageResult.Succeeded("S"));
        });

        Assert.True(called);
    }

    // --- Test doubles ---

    private sealed class FakeStage : IWorkflowStage
    {
        public string Name { get; }
        public RunPhase Phase { get; }
        public FakeStage(string name, RunPhase phase) { Name = name; Phase = phase; }
        public Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
            => Task.FromResult(StageResult.Succeeded(Name));
    }

    private sealed class MockSshService : ISshService
    {
        public bool ShouldThrow { get; set; }
        public List<(string VmName, string Content, string RemotePath)> ContentUploads { get; } = [];

        public Task<SshResult> ExecuteAsync(string vmName, string command, TimeSpan? timeout = null, CancellationToken ct = default)
            => Task.FromResult(new SshResult { ExitCode = 0 });

        public Task ScpUploadAsync(string vmName, string localPath, string remotePath, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ScpUploadContentAsync(string vmName, string content, string remotePath, CancellationToken ct = default)
        {
            if (ShouldThrow) throw new InvalidOperationException("SSH connection failed");
            ContentUploads.Add((vmName, content, remotePath));
            return Task.CompletedTask;
        }
    }
}
