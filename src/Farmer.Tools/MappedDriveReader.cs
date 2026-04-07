using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farmer.Tools;

public sealed class MappedDriveReader : IMappedDriveReader
{
    private readonly FarmerSettings _settings;
    private readonly ILogger<MappedDriveReader> _logger;

    public MappedDriveReader(IOptions<FarmerSettings> settings, ILogger<MappedDriveReader> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<string> ReadFileAsync(string vmName, string relativePath, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(vmName, relativePath);
        _logger.LogDebug("Reading mapped drive: {Path}", fullPath);

        // Retry once after SSHFS cache lag
        if (!File.Exists(fullPath))
        {
            await Task.Delay(_settings.SshfsCacheLagMs, ct);
        }

        return await File.ReadAllTextAsync(fullPath, ct);
    }

    public Task<bool> FileExistsAsync(string vmName, string relativePath, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(vmName, relativePath);
        return Task.FromResult(File.Exists(fullPath));
    }

    public async Task<string> WaitForFileAsync(string vmName, string relativePath, TimeSpan timeout, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(vmName, relativePath);
        var deadline = DateTimeOffset.UtcNow + timeout;

        _logger.LogInformation("Waiting for file: {Path} (timeout: {Timeout}s)", fullPath, timeout.TotalSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (File.Exists(fullPath))
            {
                // Extra delay for SSHFS cache to settle
                await Task.Delay(_settings.SshfsCacheLagMs, ct);
                return await File.ReadAllTextAsync(fullPath, ct);
            }

            await Task.Delay(_settings.ProgressPollIntervalMs, ct);
        }

        throw new TimeoutException($"File '{fullPath}' did not appear within {timeout.TotalSeconds}s");
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(string vmName, string relativePath, string pattern = "*", CancellationToken ct = default)
    {
        var fullPath = ResolvePath(vmName, relativePath);

        if (!Directory.Exists(fullPath))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var files = Directory.GetFiles(fullPath, pattern)
            .Select(Path.GetFileName)
            .Where(f => f is not null)
            .Select(f => f!)
            .OrderBy(f => f)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(files);
    }

    private string ResolvePath(string vmName, string relativePath)
    {
        var vm = _settings.Vms.FirstOrDefault(v => v.Name == vmName)
            ?? throw new ArgumentException($"VM '{vmName}' not found in configuration");

        return Path.Combine(vm.MappedDrivePath, relativePath.Replace('/', '\\'));
    }
}
