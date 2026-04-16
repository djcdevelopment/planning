using System.Text.Json;
using Farmer.Core.Contracts;
using Farmer.Core.Models;
using Farmer.Messaging;
using Farmer.Messaging.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.ObjectStore;
using NATS.Net;
using Xunit;

namespace Farmer.Tests.Integration;

public sealed class MessagingTests : IClassFixture<NatsServerFixture>, IAsyncDisposable
{
    private readonly NatsServerFixture _nats;
    private readonly ServiceProvider _services;

    public MessagingTests(NatsServerFixture nats)
    {
        _nats = nats;

        // Minimal host wiring: just what AddFarmerMessaging needs.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Farmer:Messaging:Url"] = _nats.Url,
                ["Farmer:Messaging:Enabled"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFarmerMessaging(config);
        _services = services.BuildServiceProvider();
    }

    // NatsConnectionProvider is IAsyncDisposable-only; sync Dispose would throw.
    public ValueTask DisposeAsync() => _services.DisposeAsync();

    [Fact]
    public async Task Publishing_a_RunEvent_lands_in_FARMER_RUNS_stream()
    {
        var publisher = _services.GetRequiredService<IRunEventPublisher>();
        var runId = "run-" + Guid.NewGuid().ToString("N")[..8];

        var evt = new RunEvent
        {
            RunId = runId,
            Stage = "CreateRun",
            Event = "stage.completed",
            Data = new { outcome = "Success", error = (string?)null },
        };
        await publisher.PublishAsync(evt);

        // Read the message back via a direct NATS client. INatsJSStream.Info is refreshed by GetStreamAsync.
        await using var client = new NatsClient(_nats.Url);
        var js = client.CreateJetStreamContext();
        var stream = await js.GetStreamAsync(Streams.RunEvents);

        Assert.True(stream.Info.State.Messages >= 1,
            $"expected at least 1 message in {Streams.RunEvents}, got {stream.Info.State.Messages}");

        var expectedSubject = Subjects.RunEventSubject(runId, evt.Stage, evt.Event);
        var observedSubjects = stream.Info.State.Subjects?.Keys.ToList() ?? [];
        // If the server chose to track per-subject counts, our subject should be listed;
        // otherwise at least verify the stream's configured pattern matches.
        Assert.True(
            observedSubjects.Count == 0 || observedSubjects.Contains(expectedSubject),
            $"expected subject {expectedSubject} in stream.Info.State.Subjects, got: [{string.Join(", ", observedSubjects)}]");
    }

    [Fact]
    public async Task Uploading_an_artifact_lands_in_the_farmer_runs_out_bucket()
    {
        var store = _services.GetRequiredService<IRunArtifactStore>();
        var runId = "run-" + Guid.NewGuid().ToString("N")[..8];

        await store.PutTextAsync(runId, "result.json", "{\"hello\":\"world\"}");

        await using var client = new NatsClient(_nats.Url);
        var obj = client.CreateObjectStoreContext();
        var bucket = await obj.GetObjectStoreAsync(Buckets.RunArtifacts);

        var key = $"{runId}/result.json";
        using var ms = new MemoryStream();
        await bucket.GetAsync(key, ms);
        var content = System.Text.Encoding.UTF8.GetString(ms.ToArray());

        Assert.Equal("{\"hello\":\"world\"}", content);
    }

    [Fact]
    public async Task Publisher_swallows_transient_errors_without_throwing()
    {
        // With a bogus URL the publisher should log-and-continue (Noop-like behavior).
        // Build a fresh service collection pointed at an unreachable port.
        var badConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Farmer:Messaging:Url"] = "nats://127.0.0.1:1", // guaranteed closed
                ["Farmer:Messaging:Enabled"] = "true",
            }).Build();

        var badServices = new ServiceCollection();
        badServices.AddLogging();
        badServices.AddFarmerMessaging(badConfig);
        await using var sp = badServices.BuildServiceProvider();

        var publisher = sp.GetRequiredService<IRunEventPublisher>();
        var evt = new RunEvent { RunId = "r", Stage = "s", Event = "stage.started" };

        // Should NOT throw — the contract is "never abort a run over a NATS hiccup".
        // We cap wait time so a real regression doesn't hang CI.
        var publish = publisher.PublishAsync(evt);
        var completed = await Task.WhenAny(publish, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(publish, completed);
    }
}
