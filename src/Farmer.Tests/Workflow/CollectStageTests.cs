using System.Text.Json;
using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Farmer.Core.Workflow.Stages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farmer.Tests.Workflow;

public class CollectStageTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static VmConfig MakeVm() => new()
    {
        Name = "claudefarm1",
        SshHost = "claudefarm1",
        SshUser = "claude",
        MappedDriveLetter = "N",
        RemoteProjectPath = "~/projects"
    };

    private static CollectStage MakeStage(MockMappedDriveReader reader, MockRunStore? store = null)
    {
        var settings = Options.Create(new FarmerSettings());
        return new CollectStage(reader, store ?? new MockRunStore(), settings,
            NullLogger<CollectStage>.Instance);
    }

    [Fact]
    public async Task Succeeds_WhenManifestHasFiles()
    {
        var manifest = new Manifest
        {
            RunId = "run-1",
            BranchName = "claudefarm1-test",
            FilesChanged = ["src/App.tsx", "package.json"]
        };

        var reader = new MockMappedDriveReader();
        reader.SetFile("output/manifest.json", JsonSerializer.Serialize(manifest, JsonOptions));

        var stage = MakeStage(reader);
        var state = new RunFlowState { RunId = "run-1", Vm = MakeVm() };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task Fails_WhenManifestTimesOut()
    {
        var reader = new MockMappedDriveReader { WaitShouldTimeout = true };

        var stage = MakeStage(reader);
        var state = new RunFlowState { RunId = "run-1", Vm = MakeVm() };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Failure, result.Outcome);
        Assert.Contains("Timed out", result.Error);
    }

    [Fact]
    public async Task Fails_WhenManifestIsInvalidJson()
    {
        var reader = new MockMappedDriveReader();
        reader.SetFile("output/manifest.json", "not valid json{{{");

        var stage = MakeStage(reader);
        var state = new RunFlowState { RunId = "run-1", Vm = MakeVm() };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Failure, result.Outcome);
        Assert.Contains("parse", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fails_WhenManifestHasNoFiles()
    {
        var manifest = new Manifest
        {
            RunId = "run-1",
            BranchName = "test",
            FilesChanged = [] // empty
        };

        var reader = new MockMappedDriveReader();
        reader.SetFile("output/manifest.json", JsonSerializer.Serialize(manifest, JsonOptions));

        var stage = MakeStage(reader);
        var state = new RunFlowState { RunId = "run-1", Vm = MakeVm() };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Failure, result.Outcome);
        Assert.Contains("no files_changed", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fails_WhenNoVm()
    {
        var reader = new MockMappedDriveReader();
        var stage = MakeStage(reader);
        var state = new RunFlowState { RunId = "run-1", Vm = null };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Failure, result.Outcome);
    }

    [Fact]
    public async Task ReadsSummary_WhenPresent()
    {
        var manifest = new Manifest
        {
            RunId = "run-1",
            FilesChanged = ["file.txt"]
        };
        var summary = new Summary
        {
            RunId = "run-1",
            Description = "Built the thing"
        };

        var reader = new MockMappedDriveReader();
        reader.SetFile("output/manifest.json", JsonSerializer.Serialize(manifest, JsonOptions));
        reader.SetFile("output/summary.json", JsonSerializer.Serialize(summary, JsonOptions));

        var stage = MakeStage(reader);
        var state = new RunFlowState { RunId = "run-1", Vm = MakeVm() };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task Succeeds_EvenWithoutSummary()
    {
        var manifest = new Manifest
        {
            RunId = "run-1",
            FilesChanged = ["file.txt"]
        };

        var reader = new MockMappedDriveReader();
        reader.SetFile("output/manifest.json", JsonSerializer.Serialize(manifest, JsonOptions));
        // No summary file

        var stage = MakeStage(reader);
        var state = new RunFlowState { RunId = "run-1", Vm = MakeVm() };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
    }

    // --- Test doubles ---

    private sealed class MockMappedDriveReader : IMappedDriveReader
    {
        private readonly Dictionary<string, string> _files = new();
        public bool WaitShouldTimeout { get; set; }

        public void SetFile(string relativePath, string content)
        {
            _files[NormalizePath(relativePath)] = content;
        }

        public Task<string> ReadFileAsync(string vmName, string relativePath, CancellationToken ct = default)
        {
            var key = NormalizePath(relativePath);
            if (!_files.TryGetValue(key, out var content))
                throw new FileNotFoundException($"Mock file not found: {relativePath}");
            return Task.FromResult(content);
        }

        public Task<bool> FileExistsAsync(string vmName, string relativePath, CancellationToken ct = default)
        {
            return Task.FromResult(_files.ContainsKey(NormalizePath(relativePath)));
        }

        public Task<string> WaitForFileAsync(string vmName, string relativePath, TimeSpan timeout, CancellationToken ct = default)
        {
            if (WaitShouldTimeout)
                throw new TimeoutException("Mock timeout");

            return ReadFileAsync(vmName, relativePath, ct);
        }

        public Task<IReadOnlyList<string>> ListFilesAsync(string vmName, string relativePath, string pattern = "*", CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(
                _files.Keys.Where(k => k.StartsWith(NormalizePath(relativePath))).ToList());
        }

        private static string NormalizePath(string p) => p.Replace('\\', '/');
    }

    private sealed class MockRunStore : IRunStore
    {
        public Task SaveRunRequestAsync(RunRequest r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RunRequest?> GetRunRequestAsync(string id, CancellationToken ct = default) => Task.FromResult<RunRequest?>(null);
        public Task SaveTaskPacketAsync(TaskPacket p, CancellationToken ct = default) => Task.CompletedTask;
        public Task<TaskPacket?> GetTaskPacketAsync(string id, CancellationToken ct = default) => Task.FromResult<TaskPacket?>(null);
        public Task SaveRunStatusAsync(RunStatus s, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RunStatus?> GetRunStatusAsync(string id, CancellationToken ct = default) => Task.FromResult<RunStatus?>(null);
        public Task SaveCostReportAsync(CostReport r, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveReviewVerdictAsync(ReviewVerdict v, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListRunIdsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
