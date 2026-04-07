using Farmer.Core.Config;

namespace Farmer.Core.Contracts;

public enum VmState
{
    Available,
    Reserved,
    Busy,
    Draining,
    Error
}

public interface IVmManager
{
    Task<VmConfig?> ReserveAsync(CancellationToken ct = default);

    Task ReleaseAsync(string vmName, CancellationToken ct = default);

    Task MarkBusyAsync(string vmName, CancellationToken ct = default);

    Task MarkErrorAsync(string vmName, string reason, CancellationToken ct = default);

    VmState GetState(string vmName);

    IReadOnlyList<VmConfig> GetAllVms();
}
