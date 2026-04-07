using System.Text.Json;
using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Layout;
using Farmer.Core.Models;
using Microsoft.Extensions.Options;

namespace Farmer.Tools;

public sealed class FileRunStore : IRunStore
{
    private readonly FarmerSettings _settings;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public FileRunStore(IOptions<FarmerSettings> settings)
    {
        _settings = settings.Value;
    }

    public async Task SaveRunRequestAsync(RunRequest request, CancellationToken ct = default)
    {
        RunDirectoryLayout.EnsureRunDirectory(_settings.RunStorePath, request.RunId);
        var path = RunDirectoryLayout.RunRequestFile(_settings.RunStorePath, request.RunId);
        await WriteJsonAsync(path, request, ct);
    }

    public async Task<RunRequest?> GetRunRequestAsync(string runId, CancellationToken ct = default)
    {
        var path = RunDirectoryLayout.RunRequestFile(_settings.RunStorePath, runId);
        return await ReadJsonAsync<RunRequest>(path, ct);
    }

    public async Task SaveTaskPacketAsync(TaskPacket packet, CancellationToken ct = default)
    {
        RunDirectoryLayout.EnsureRunDirectory(_settings.RunStorePath, packet.RunId);
        var path = RunDirectoryLayout.RunTaskPacketFile(_settings.RunStorePath, packet.RunId);
        await WriteJsonAsync(path, packet, ct);
    }

    public async Task<TaskPacket?> GetTaskPacketAsync(string runId, CancellationToken ct = default)
    {
        var path = RunDirectoryLayout.RunTaskPacketFile(_settings.RunStorePath, runId);
        return await ReadJsonAsync<TaskPacket>(path, ct);
    }

    public async Task SaveRunStatusAsync(RunStatus status, CancellationToken ct = default)
    {
        RunDirectoryLayout.EnsureRunDirectory(_settings.RunStorePath, status.RunId);
        var path = RunDirectoryLayout.RunStatusFile(_settings.RunStorePath, status.RunId);
        await WriteJsonAsync(path, status, ct);
    }

    public async Task<RunStatus?> GetRunStatusAsync(string runId, CancellationToken ct = default)
    {
        var path = RunDirectoryLayout.RunStatusFile(_settings.RunStorePath, runId);
        return await ReadJsonAsync<RunStatus>(path, ct);
    }

    public async Task SaveCostReportAsync(CostReport report, CancellationToken ct = default)
    {
        RunDirectoryLayout.EnsureRunDirectory(_settings.RunStorePath, report.RunId);
        var path = RunDirectoryLayout.RunCostReportFile(_settings.RunStorePath, report.RunId);
        await WriteJsonAsync(path, report, ct);
    }

    public async Task SaveReviewVerdictAsync(ReviewVerdict verdict, CancellationToken ct = default)
    {
        RunDirectoryLayout.EnsureRunDirectory(_settings.RunStorePath, verdict.RunId);
        var path = RunDirectoryLayout.RunReviewFile(_settings.RunStorePath, verdict.RunId);
        await WriteJsonAsync(path, verdict, ct);
    }

    public Task<IReadOnlyList<string>> ListRunIdsAsync(CancellationToken ct = default)
    {
        var runStorePath = _settings.RunStorePath;
        if (!Directory.Exists(runStorePath))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var dirs = Directory.GetDirectories(runStorePath)
            .Select(Path.GetFileName)
            .Where(d => d is not null)
            .Select(d => d!)
            .OrderByDescending(d => d)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(dirs);
    }

    private static async Task WriteJsonAsync<T>(string path, T obj, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(obj, JsonOptions);
        // Atomic write: write to temp, then rename
        var tmpPath = path + ".tmp";
        await File.WriteAllTextAsync(tmpPath, json, ct);
        File.Move(tmpPath, path, overwrite: true);
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return default;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }
}
