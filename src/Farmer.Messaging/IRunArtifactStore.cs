namespace Farmer.Messaging;

/// <summary>
/// Publishes large run artifacts (events.jsonl, result.json, review.json, retro docs)
/// to NATS ObjectStore. Small per-stage events go through IRunEventPublisher; anything
/// &gt; ~4KB or meant to be downloaded post-hoc goes here.
/// </summary>
public interface IRunArtifactStore
{
    /// <summary>Upload raw bytes under key "{runId}/{name}" in the farmer-runs-out bucket.</summary>
    Task PutAsync(string runId, string name, ReadOnlyMemory<byte> content, CancellationToken ct = default);

    /// <summary>Upload UTF-8 text under key "{runId}/{name}". Convenience wrapper over PutAsync.</summary>
    Task PutTextAsync(string runId, string name, string content, CancellationToken ct = default);
}
