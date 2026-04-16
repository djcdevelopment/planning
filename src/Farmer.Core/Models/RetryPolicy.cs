using System.Text.Json.Serialization;

namespace Farmer.Core.Models;

/// <summary>
/// Opt-in retry policy for a single /trigger invocation. When Enabled=false (default),
/// the /trigger handler runs exactly one attempt and the response shape is unchanged.
/// When Enabled=true, the handler loops up to MaxAttempts times, re-running the whole
/// 7-stage workflow each time, and stops early if the retrospective verdict isn't in
/// RetryOnVerdicts.
///
/// Phase 7 honors this in-process; ADR-011 defers NATS-event-driven retry to later.
/// </summary>
public sealed class RetryPolicy
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>Total attempts allowed, including the first. 2 = "try once, retry at most once".</summary>
    [JsonPropertyName("max_attempts")]
    public int MaxAttempts { get; set; } = 2;

    /// <summary>Verdict strings ("Retry", "Reject", "Accept") that should trigger another attempt. Default: ["Retry"].</summary>
    [JsonPropertyName("retry_on_verdicts")]
    public List<string> RetryOnVerdicts { get; set; } = new() { "Retry" };
}
