using Farmer.Core.Contracts;
using Farmer.Core.Models;

namespace Farmer.Tests.TestHelpers;

/// <summary>
/// In-memory IRunStore for unit tests. Previously copy-pasted as private nested
/// classes across 4 test files; extracted here so there's one copy to maintain.
/// </summary>
public sealed class InMemoryRunStore : IRunStore
{
    private readonly Dictionary<string, RunRequest> _requests = new();
    private readonly Dictionary<string, TaskPacket> _packets = new();
    private readonly Dictionary<string, RunStatus> _statuses = new();

    public Task SaveRunRequestAsync(RunRequest r, CancellationToken ct = default) { _requests[r.RunId] = r; return Task.CompletedTask; }
    public Task<RunRequest?> GetRunRequestAsync(string id, CancellationToken ct = default) { _requests.TryGetValue(id, out var r); return Task.FromResult(r); }
    public Task SaveTaskPacketAsync(TaskPacket p, CancellationToken ct = default) { _packets[p.RunId] = p; return Task.CompletedTask; }
    public Task<TaskPacket?> GetTaskPacketAsync(string id, CancellationToken ct = default) { _packets.TryGetValue(id, out var p); return Task.FromResult(p); }
    public Task SaveRunStateAsync(RunStatus s, CancellationToken ct = default) { _statuses[s.RunId] = s; return Task.CompletedTask; }
    public Task<RunStatus?> GetRunStateAsync(string id, CancellationToken ct = default) { _statuses.TryGetValue(id, out var s); return Task.FromResult(s); }
    public Task SaveCostReportAsync(CostReport r, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveReviewVerdictAsync(ReviewVerdict v, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<string>> ListRunIdsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(_requests.Keys.ToList());
}
