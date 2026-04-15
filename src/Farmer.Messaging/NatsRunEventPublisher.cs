using System.Text.Json;
using Farmer.Core.Contracts;
using Farmer.Core.Models;
using Farmer.Messaging.Contracts;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Net;

namespace Farmer.Messaging;

/// <summary>
/// Publishes RunEvents to the FARMER_RUNS JetStream stream. Tolerant of NATS outages —
/// logs and continues so a server hiccup never aborts a run (events.jsonl on disk is
/// still the durable record during MVP).
/// </summary>
public sealed class NatsRunEventPublisher : IRunEventPublisher
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly NatsConnectionProvider _provider;
    private readonly ILogger<NatsRunEventPublisher> _log;

    public NatsRunEventPublisher(NatsConnectionProvider provider, ILogger<NatsRunEventPublisher> log)
    {
        _provider = provider;
        _log = log;
    }

    public async Task PublishAsync(RunEvent evt, CancellationToken ct = default)
    {
        if (!_provider.Enabled) return;

        try
        {
            var client = await _provider.GetClientAsync(ct);
            var js = client.CreateJetStreamContext();
            var subject = Subjects.RunEventSubject(evt.RunId, evt.Stage, evt.Event);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(evt, JsonOpts);
            await js.PublishAsync(subject, bytes, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to publish run event run_id={RunId} stage={Stage} event={Event}; continuing without.",
                evt.RunId, evt.Stage, evt.Event);
        }
    }
}

