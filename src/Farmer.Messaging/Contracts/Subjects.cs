namespace Farmer.Messaging.Contracts;

/// <summary>
/// Canonical NATS subjects for Farmer. Runs are the only first-class workload type today;
/// add new top-level namespaces (e.g. farmer.qa.*) as new workloads appear.
/// </summary>
public static class Subjects
{
    /// <summary>Submit a new run for execution. Reserved for the future NATS ingress path; the HTTP /trigger endpoint stays synchronous for MVP.</summary>
    public const string SubmitRun = "farmer.rpc.submitRun";

    /// <summary>Event-per-stage subject prefix. Concrete subject: farmer.events.run.{runId}.{stage}.{status}</summary>
    public const string RunEventsPrefix = "farmer.events.run";

    /// <summary>JetStream capture wildcard for every run event.</summary>
    public const string RunEventsWildcard = "farmer.events.run.>";

    public static string RunEventSubject(string runId, string stage, string status)
        => $"{RunEventsPrefix}.{runId}.{stage}.{status}";
}

public static class Streams
{
    /// <summary>JetStream stream that captures every run event. Stored to disk, 24h retention.</summary>
    public const string RunEvents = "FARMER_RUNS";
}

public static class Buckets
{
    /// <summary>ObjectStore bucket for run artifacts (events.jsonl, state.json, result.json, review.json, retro docs).</summary>
    public const string RunArtifacts = "farmer-runs-out";
}
