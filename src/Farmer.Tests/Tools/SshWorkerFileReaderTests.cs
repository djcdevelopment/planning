using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farmer.Tests.Tools;

public class SshWorkerFileReaderTests
{
    private const string VmName = "claudefarm1";
    private const string RemoteRoot = "/home/claude/projects";

    private static FarmerSettings MakeSettings() => new()
    {
        ProgressPollIntervalMs = 10,  // fast tests
        SshCommandTimeoutSeconds = 30,
        Vms =
        [
            new VmConfig
            {
                Name = VmName,
                SshHost = VmName,
                SshUser = "claude",
                RemoteProjectPath = RemoteRoot,
            }
        ],
    };

    private static SshWorkerFileReader MakeReader(MockSshService ssh, FarmerSettings? settings = null) =>
        new(ssh, Options.Create(settings ?? MakeSettings()), NullLogger<SshWorkerFileReader>.Instance);

    // --- ReadFileAsync ---

    [Fact]
    public async Task ReadFileAsync_ReturnsStdout_OnExitZero()
    {
        var ssh = new MockSshService();
        ssh.Enqueue(new SshResult { ExitCode = 0, StdOut = "hello world" });
        var reader = MakeReader(ssh);

        var content = await reader.ReadFileAsync(VmName, "output/manifest.json");

        Assert.Equal("hello world", content);
    }

    [Fact]
    public async Task ReadFileAsync_Throws_OnNonZeroExit()
    {
        var ssh = new MockSshService();
        ssh.Enqueue(new SshResult { ExitCode = 1, StdErr = "cat: No such file or directory" });
        var reader = MakeReader(ssh);

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            reader.ReadFileAsync(VmName, "output/missing.json"));

        Assert.Contains("No such file", ex.Message);
    }

    [Fact]
    public async Task ReadFileAsync_BuildsCatCommand_WithQuotedAbsolutePath()
    {
        var ssh = new MockSshService();
        ssh.Enqueue(new SshResult { ExitCode = 0, StdOut = "{}" });
        var reader = MakeReader(ssh);

        await reader.ReadFileAsync(VmName, "output/manifest.json");

        Assert.Single(ssh.Commands);
        var cmd = ssh.Commands[0].Command;
        Assert.StartsWith("cat -- '", cmd);
        // Joined absolute path, separators normalized to /.
        Assert.Contains($"'{RemoteRoot}/output/manifest.json'", cmd);
    }

    [Fact]
    public async Task ReadFileAsync_NormalizesWindowsSeparators_ToForwardSlashes()
    {
        var ssh = new MockSshService();
        ssh.Enqueue(new SshResult { ExitCode = 0, StdOut = "" });
        var reader = MakeReader(ssh);

        // CollectStage uses Path.Combine which on Windows produces `output\manifest.json`.
        await reader.ReadFileAsync(VmName, "output\\manifest.json");

        Assert.Contains($"'{RemoteRoot}/output/manifest.json'", ssh.Commands[0].Command);
        Assert.DoesNotContain("\\", ssh.Commands[0].Command);
    }

    [Fact]
    public async Task ReadFileAsync_Throws_OnUnknownVm()
    {
        var ssh = new MockSshService();
        var reader = MakeReader(ssh);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.ReadFileAsync("ghost-vm", "x"));
    }

    // --- FileExistsAsync ---

    [Fact]
    public async Task FileExistsAsync_Returns_True_On_1Output()
    {
        var ssh = new MockSshService();
        ssh.Enqueue(new SshResult { ExitCode = 0, StdOut = "1\n" });
        var reader = MakeReader(ssh);

        var exists = await reader.FileExistsAsync(VmName, "output/summary.json");

        Assert.True(exists);
        Assert.Contains("test -f ", ssh.Commands[0].Command);
        Assert.Contains("echo 1", ssh.Commands[0].Command);
        Assert.Contains("echo 0", ssh.Commands[0].Command);
    }

    [Fact]
    public async Task FileExistsAsync_Returns_False_On_0Output()
    {
        var ssh = new MockSshService();
        ssh.Enqueue(new SshResult { ExitCode = 0, StdOut = "0\n" });
        var reader = MakeReader(ssh);

        Assert.False(await reader.FileExistsAsync(VmName, "output/missing.json"));
    }

    // --- WaitForFileAsync ---

    [Fact]
    public async Task WaitForFileAsync_ReturnsContents_AfterPollHit()
    {
        var ssh = new MockSshService();
        // First poll: not found. Second poll: found. Then read.
        ssh.Enqueue(new SshResult { ExitCode = 0, StdOut = "0\n" });  // test -f #1
        ssh.Enqueue(new SshResult { ExitCode = 0, StdOut = "1\n" });  // test -f #2
        ssh.Enqueue(new SshResult { ExitCode = 0, StdOut = "the contents" });  // cat
        var reader = MakeReader(ssh);

        var content = await reader.WaitForFileAsync(
            VmName, "output/manifest.json", TimeSpan.FromSeconds(5));

        Assert.Equal("the contents", content);
        Assert.Equal(3, ssh.Commands.Count);
    }

    [Fact]
    public async Task WaitForFileAsync_Throws_OnTimeout()
    {
        var ssh = new MockSshService();
        // Always respond "not found" so the loop never satisfies.
        ssh.DefaultResult = new SshResult { ExitCode = 0, StdOut = "0\n" };
        var reader = MakeReader(ssh);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            reader.WaitForFileAsync(VmName, "output/nope.json", TimeSpan.FromMilliseconds(50)));
    }

    // --- ListFilesAsync ---

    [Fact]
    public async Task ListFilesAsync_SplitsOnNewlines_AndSorts()
    {
        var ssh = new MockSshService();
        ssh.Enqueue(new SshResult { ExitCode = 0, StdOut = "b.md\na.md\nc.md\n" });
        var reader = MakeReader(ssh);

        var files = await reader.ListFilesAsync(VmName, "plans", "*.md");

        Assert.Equal(["a.md", "b.md", "c.md"], files);
    }

    [Fact]
    public async Task ListFilesAsync_ReturnsEmpty_OnNonZeroExit()
    {
        var ssh = new MockSshService();
        ssh.Enqueue(new SshResult { ExitCode = 1, StdErr = "cd: No such file or directory" });
        var reader = MakeReader(ssh);

        var files = await reader.ListFilesAsync(VmName, "does/not/exist");

        Assert.Empty(files);
    }

    [Fact]
    public async Task ListFilesAsync_UsesCd_And_Ls1_WithPattern()
    {
        var ssh = new MockSshService();
        ssh.Enqueue(new SshResult { ExitCode = 0, StdOut = "" });
        var reader = MakeReader(ssh);

        await reader.ListFilesAsync(VmName, "plans", "*.md");

        var cmd = ssh.Commands[0].Command;
        Assert.Contains("cd '", cmd);
        Assert.Contains($"'{RemoteRoot}/plans'", cmd);
        Assert.Contains("ls -1 -- '*.md'", cmd);
    }

    // --- Path quoting ---

    [Fact]
    public void ShellQuote_WrapsPathInSingleQuotes()
    {
        Assert.Equal("'hello'", SshWorkerFileReader.ShellQuote("hello"));
    }

    [Fact]
    public void ShellQuote_EscapesInternalSingleQuotes()
    {
        // Bash idiom: close-quote, emit backslash-quote, reopen.
        Assert.Equal(@"'it'\''s'", SshWorkerFileReader.ShellQuote("it's"));
    }

    // --- Test double ---

    /// <summary>
    /// Queue-based fake: each call to ExecuteAsync dequeues the next seeded
    /// result, or falls back to <see cref="DefaultResult"/> when the queue is
    /// empty (for loop-heavy tests like the WaitFor timeout case).
    /// </summary>
    private sealed class MockSshService : ISshService
    {
        private readonly Queue<SshResult> _results = new();
        public SshResult DefaultResult { get; set; } = new() { ExitCode = 0, StdOut = "" };
        public List<(string VmName, string Command)> Commands { get; } = [];

        public void Enqueue(SshResult r) => _results.Enqueue(r);

        public Task<SshResult> ExecuteAsync(string vmName, string command, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            Commands.Add((vmName, command));
            var r = _results.Count > 0 ? _results.Dequeue() : DefaultResult;
            return Task.FromResult(r);
        }

        public Task ScpUploadAsync(string vmName, string localPath, string remotePath, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ScpUploadContentAsync(string vmName, string content, string remotePath, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
