using System.Text;
using Farmer.Messaging.Contracts;
using Microsoft.Extensions.Logging;
using NATS.Client.ObjectStore;
using NATS.Client.ObjectStore.Models;
using NATS.Net;

namespace Farmer.Messaging;

/// <summary>
/// Writes run artifacts (events.jsonl, result.json, review.json, etc.) to the
/// farmer-runs-out ObjectStore bucket. Keys are "{runId}/{name}".
/// </summary>
public sealed class NatsRunArtifactStore : IRunArtifactStore
{
    private readonly NatsConnectionProvider _provider;
    private readonly ILogger<NatsRunArtifactStore> _log;

    public NatsRunArtifactStore(NatsConnectionProvider provider, ILogger<NatsRunArtifactStore> log)
    {
        _provider = provider;
        _log = log;
    }

    public async Task PutAsync(string runId, string name, ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        if (!_provider.Enabled) return;
        try
        {
            var client = await _provider.GetClientAsync(ct);
            var obj = client.CreateObjectStoreContext();
            var bucket = await obj.GetObjectStoreAsync(Buckets.RunArtifacts, ct);
            var key = $"{runId}/{name}";
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            await bucket.PutAsync(new ObjectMetadata { Name = key }, stream, leaveOpen: true, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to upload artifact run_id={RunId} name={Name}; continuing without.", runId, name);
        }
    }

    public Task PutTextAsync(string runId, string name, string content, CancellationToken ct = default)
        => PutAsync(runId, name, Encoding.UTF8.GetBytes(content), ct);
}

public sealed class NoopRunArtifactStore : IRunArtifactStore
{
    public Task PutAsync(string runId, string name, ReadOnlyMemory<byte> content, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task PutTextAsync(string runId, string name, string content, CancellationToken ct = default)
        => Task.CompletedTask;
}
