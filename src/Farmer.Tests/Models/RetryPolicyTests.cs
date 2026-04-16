using System.Text.Json;
using Farmer.Core.Models;
using Xunit;

namespace Farmer.Tests.Models;

public class RetryPolicyTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void Defaults_are_safe_for_opt_in()
    {
        var p = new RetryPolicy();
        Assert.False(p.Enabled);
        Assert.Equal(2, p.MaxAttempts);
        Assert.Equal(new[] { "Retry" }, p.RetryOnVerdicts);
    }

    [Fact]
    public void Round_trips_via_snake_case_json()
    {
        var original = new RetryPolicy
        {
            Enabled = true,
            MaxAttempts = 3,
            RetryOnVerdicts = new() { "Retry", "Reject" },
        };
        var json = JsonSerializer.Serialize(original, Opts);

        // Contract: snake_case keys, because worker.sh / other consumers read JSON directly.
        Assert.Contains("\"enabled\":true", json);
        Assert.Contains("\"max_attempts\":3", json);
        Assert.Contains("\"retry_on_verdicts\":[", json);

        var roundTripped = JsonSerializer.Deserialize<RetryPolicy>(json, Opts);
        Assert.NotNull(roundTripped);
        Assert.True(roundTripped!.Enabled);
        Assert.Equal(3, roundTripped.MaxAttempts);
        Assert.Equal(new[] { "Retry", "Reject" }, roundTripped.RetryOnVerdicts);
    }

    [Fact]
    public void RunRequest_carries_RetryPolicy_through_serialization()
    {
        var req = new RunRequest
        {
            RunId = "run-123",
            WorkRequestName = "demo",
            RetryPolicy = new RetryPolicy { Enabled = true, MaxAttempts = 2 },
            Feedback = "prior attempt flagged missing tests",
        };
        var json = JsonSerializer.Serialize(req, Opts);

        Assert.Contains("\"retry_policy\":{", json);
        Assert.Contains("\"feedback\":\"prior attempt flagged missing tests\"", json);

        var back = JsonSerializer.Deserialize<RunRequest>(json, Opts);
        Assert.NotNull(back);
        Assert.NotNull(back!.RetryPolicy);
        Assert.True(back.RetryPolicy!.Enabled);
        Assert.Equal("prior attempt flagged missing tests", back.Feedback);
    }
}
