using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Client.ObjectStore;
using NATS.Client.ObjectStore.Models;
using NATS.Net;
using Farmer.Messaging.Contracts;

namespace Farmer.Messaging;

/// <summary>
/// Singleton that owns the live <see cref="NatsClient"/>, JetStream context, and
/// ObjectStore context. Lazily ensures the FARMER_RUNS stream and farmer-runs-out
/// bucket exist on first use.
/// </summary>
public sealed class NatsConnectionProvider : IAsyncDisposable
{
    private readonly NatsSettings _settings;
    private readonly ILogger<NatsConnectionProvider> _log;
    private readonly SemaphoreSlim _bootstrapLock = new(1, 1);
    private NatsClient? _client;
    private bool _bootstrapped;

    public NatsConnectionProvider(IOptions<NatsSettings> settings, ILogger<NatsConnectionProvider> log)
    {
        _settings = settings.Value;
        _log = log;
    }

    public bool Enabled => _settings.Enabled && !string.IsNullOrWhiteSpace(_settings.Url);

    public async ValueTask<NatsClient> GetClientAsync(CancellationToken ct = default)
    {
        if (!Enabled)
            throw new InvalidOperationException("NATS messaging is disabled (Farmer:Messaging:Enabled=false or Url missing). Check the caller for an `if (provider.Enabled)` guard.");

        if (_client is null)
        {
            await _bootstrapLock.WaitAsync(ct);
            try
            {
                _client ??= new NatsClient(_settings.Url);
                if (!_bootstrapped)
                {
                    await EnsureTopologyAsync(_client, ct);
                    _bootstrapped = true;
                }
            }
            finally { _bootstrapLock.Release(); }
        }

        return _client;
    }

    private async Task EnsureTopologyAsync(NatsClient client, CancellationToken ct)
    {
        try
        {
            var js = client.CreateJetStreamContext();
            await js.CreateOrUpdateStreamAsync(new StreamConfig(Streams.RunEvents, [Subjects.RunEventsWildcard])
            {
                Retention = StreamConfigRetention.Limits,
                MaxAge = TimeSpan.FromHours(24),
                Storage = StreamConfigStorage.File,
            }, ct);

            var obj = client.CreateObjectStoreContext();
            try { await obj.GetObjectStoreAsync(Buckets.RunArtifacts, ct); }
            catch
            {
                await obj.CreateObjectStoreAsync(
                    new NatsObjConfig(Buckets.RunArtifacts) { Storage = NatsObjStorageType.File },
                    ct);
            }

            _log.LogInformation("NATS topology ready: stream={Stream} bucket={Bucket} url={Url}",
                Streams.RunEvents, Buckets.RunArtifacts, _settings.Url);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to bootstrap NATS topology at {Url}. Messaging calls will throw until the server is reachable.", _settings.Url);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }
}
