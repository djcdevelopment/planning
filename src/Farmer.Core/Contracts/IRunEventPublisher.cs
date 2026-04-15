using Farmer.Core.Models;

namespace Farmer.Core.Contracts;

/// <summary>
/// Publishes stage-lifecycle events to an external event bus (NATS JetStream
/// in production, noop in unit tests). Called by EventingMiddleware alongside
/// the durable events.jsonl write — not a replacement for it during MVP.
/// </summary>
public interface IRunEventPublisher
{
    Task PublishAsync(RunEvent evt, CancellationToken ct = default);
}

/// <summary>No-op default. Keeps EventingMiddleware runnable without external deps.</summary>
public sealed class NoopRunEventPublisher : IRunEventPublisher
{
    public static readonly NoopRunEventPublisher Instance = new();
    public Task PublishAsync(RunEvent evt, CancellationToken ct = default) => Task.CompletedTask;
}
