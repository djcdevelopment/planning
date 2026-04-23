using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farmer.Tools;

/// <summary>
/// SSH-based implementation of <see cref="IMappedDriveReader"/>. Replaces the
/// mapped-drive (WinFsp/SSHFS-Win) backend with direct SSH reads against the
/// worker VM. Each operation opens-closes via <see cref="ISshService"/>, which
/// already pools <c>SshClient</c>s per-VM. This scales to N workers without
/// needing N drive letters (see Phase 7 Stream D spec).
/// </summary>
public sealed class SshWorkerFileReader : IMappedDriveReader
{
    private readonly ISshService _ssh;
    private readonly FarmerSettings _settings;
    private readonly ILogger<SshWorkerFileReader> _logger;

    public SshWorkerFileReader(
        ISshService ssh,
        IOptions<FarmerSettings> settings,
        ILogger<SshWorkerFileReader> logger)
    {
        _ssh = ssh;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<string> ReadFileAsync(string vmName, string relativePath, CancellationToken ct = default)
    {
        var vm = ResolveVm(vmName);
        var remotePath = ResolveRemotePath(vm, relativePath);
        var cmd = $"cat -- {ShellQuote(remotePath)}";

        _logger.LogDebug("SSH read [{Vm}]: {Path}", vmName, remotePath);

        var result = await _ssh.ExecuteAsync(vmName, cmd, ct: ct);
        if (!result.Success)
        {
            throw new FileNotFoundException(
                $"SSH read failed for '{remotePath}' on {vmName} (exit {result.ExitCode}): {result.StdErr}",
                remotePath);
        }

        return result.StdOut;
    }

    public async Task<bool> FileExistsAsync(string vmName, string relativePath, CancellationToken ct = default)
    {
        var vm = ResolveVm(vmName);
        var remotePath = ResolveRemotePath(vm, relativePath);
        // test -f exits 0 if file exists; wrap in an echo so we get a deterministic
        // stdout regardless of exit status (ssh.ExecuteAsync() returns exit=0 for
        // both branches of the || ).
        var cmd = $"test -f {ShellQuote(remotePath)} && echo 1 || echo 0";

        var result = await _ssh.ExecuteAsync(vmName, cmd, ct: ct);
        return result.StdOut.Trim() == "1";
    }

    public async Task<string> WaitForFileAsync(string vmName, string relativePath, TimeSpan timeout, CancellationToken ct = default)
    {
        var vm = ResolveVm(vmName);
        var remotePath = ResolveRemotePath(vm, relativePath);
        var deadline = DateTimeOffset.UtcNow + timeout;

        _logger.LogInformation("Waiting for file via SSH: {Vm}:{Path} (timeout: {Timeout}s)",
            vmName, remotePath, timeout.TotalSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (await FileExistsAsync(vmName, relativePath, ct))
            {
                // SSH reads are consistent — no SSHFS cache lag to wait for.
                return await ReadFileAsync(vmName, relativePath, ct);
            }

            await Task.Delay(_settings.ProgressPollIntervalMs, ct);
        }

        throw new TimeoutException($"File '{remotePath}' did not appear on {vmName} within {timeout.TotalSeconds}s");
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(string vmName, string relativePath, string pattern = "*", CancellationToken ct = default)
    {
        var vm = ResolveVm(vmName);
        var remoteDir = ResolveRemotePath(vm, relativePath);

        // `cd <dir> && ls -1 <pattern>` so output lines are basenames (not `dir/foo`),
        // matching MappedDriveReader's contract of returning filenames only. The
        // 2>/dev/null on `cd` swallows "No such file or directory" so a missing dir
        // just yields exit != 0 → empty list (parity with Directory.Exists guard).
        var cmd = $"cd {ShellQuote(remoteDir)} 2>/dev/null && ls -1 -- {ShellQuote(pattern)} 2>/dev/null";

        var result = await _ssh.ExecuteAsync(vmName, cmd, ct: ct);
        if (!result.Success)
            return Array.Empty<string>();

        var files = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .Where(f => f.Length > 0)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        return files;
    }

    private VmConfig ResolveVm(string vmName)
    {
        return _settings.Vms.FirstOrDefault(v => v.Name == vmName)
            ?? throw new ArgumentException($"VM '{vmName}' not found in configuration");
    }

    /// <summary>
    /// Joins <see cref="VmConfig.RemoteProjectPath"/> with the caller-supplied
    /// relative path, normalizing <c>\</c> to <c>/</c> so Windows-style
    /// <see cref="Path.Combine"/> output from call sites works on the Linux
    /// target. Does NOT expand <c>~</c>; callers configure
    /// <c>RemoteProjectPath</c> as an absolute POSIX path (see CLAUDE.md
    /// "SSH uses absolute paths" gotcha).
    /// </summary>
    private static string ResolveRemotePath(VmConfig vm, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var baseDir = vm.RemoteProjectPath.TrimEnd('/');
        return $"{baseDir}/{normalized}";
    }

    /// <summary>
    /// Single-quote a shell argument safely. The bash idiom for embedding a
    /// literal <c>'</c> inside a single-quoted string is <c>'\''</c>: close the
    /// current quote, emit an escaped quote, reopen. Worker-generated paths
    /// never contain quotes, but we defend so a malformed relativePath can't
    /// inject commands.
    /// </summary>
    // Exposed public static so tests can exercise the escape logic directly
    // without building a full ssh harness. Kept on this class (rather than a
    // shared utility) because nothing else in the solution shell-quotes today.
    public static string ShellQuote(string value)
    {
        return "'" + value.Replace("'", "'\\''") + "'";
    }
}
