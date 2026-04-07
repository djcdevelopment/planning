using System.Collections.Concurrent;
using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farmer.Tools;

public sealed class VmManager : IVmManager
{
    private readonly ConcurrentDictionary<string, VmState> _states = new();
    private readonly Dictionary<string, VmConfig> _vms = new();
    private readonly ILogger<VmManager> _logger;
    private readonly object _reserveLock = new();

    public VmManager(IOptions<FarmerSettings> settings, ILogger<VmManager> logger)
    {
        _logger = logger;
        foreach (var vm in settings.Value.Vms)
        {
            _vms[vm.Name] = vm;
            _states[vm.Name] = VmState.Available;
        }
    }

    public Task<VmConfig?> ReserveAsync(CancellationToken ct = default)
    {
        lock (_reserveLock)
        {
            foreach (var (name, state) in _states)
            {
                if (state == VmState.Available)
                {
                    _states[name] = VmState.Reserved;
                    _logger.LogInformation("VM [{Vm}] reserved", name);
                    return Task.FromResult<VmConfig?>(_vms[name]);
                }
            }
        }

        _logger.LogWarning("No VMs available for reservation");
        return Task.FromResult<VmConfig?>(null);
    }

    public Task ReleaseAsync(string vmName, CancellationToken ct = default)
    {
        if (!_states.ContainsKey(vmName))
            throw new ArgumentException($"VM '{vmName}' not found");

        _states[vmName] = VmState.Available;
        _logger.LogInformation("VM [{Vm}] released", vmName);
        return Task.CompletedTask;
    }

    public Task MarkBusyAsync(string vmName, CancellationToken ct = default)
    {
        if (!_states.ContainsKey(vmName))
            throw new ArgumentException($"VM '{vmName}' not found");

        _states[vmName] = VmState.Busy;
        _logger.LogInformation("VM [{Vm}] marked busy", vmName);
        return Task.CompletedTask;
    }

    public Task MarkErrorAsync(string vmName, string reason, CancellationToken ct = default)
    {
        if (!_states.ContainsKey(vmName))
            throw new ArgumentException($"VM '{vmName}' not found");

        _states[vmName] = VmState.Error;
        _logger.LogError("VM [{Vm}] marked error: {Reason}", vmName, reason);
        return Task.CompletedTask;
    }

    public VmState GetState(string vmName)
    {
        if (!_states.TryGetValue(vmName, out var state))
            throw new ArgumentException($"VM '{vmName}' not found");

        return state;
    }

    public IReadOnlyList<VmConfig> GetAllVms() => _vms.Values.ToList();
}
