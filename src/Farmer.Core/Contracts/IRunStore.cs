using Farmer.Core.Models;

namespace Farmer.Core.Contracts;

public interface IRunStore
{
    Task SaveRunRequestAsync(RunRequest request, CancellationToken ct = default);
    Task<RunRequest?> GetRunRequestAsync(string runId, CancellationToken ct = default);

    Task SaveTaskPacketAsync(TaskPacket packet, CancellationToken ct = default);
    Task<TaskPacket?> GetTaskPacketAsync(string runId, CancellationToken ct = default);

    Task SaveRunStateAsync(RunStatus status, CancellationToken ct = default);
    Task<RunStatus?> GetRunStateAsync(string runId, CancellationToken ct = default);

    Task SaveCostReportAsync(CostReport report, CancellationToken ct = default);
    Task SaveReviewVerdictAsync(ReviewVerdict verdict, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListRunIdsAsync(CancellationToken ct = default);
}
