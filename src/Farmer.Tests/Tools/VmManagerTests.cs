using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farmer.Tests.Tools;

public class VmManagerTests
{
    private static VmManager MakeManager(params string[] vmNames)
    {
        var vms = vmNames.Select(n => new VmConfig
        {
            Name = n,
            SshHost = n,
            SshUser = "claude",
            MappedDriveLetter = n[^1].ToString().ToUpper(),
            RemoteProjectPath = "~/projects"
        }).ToList();

        var settings = Options.Create(new FarmerSettings { Vms = vms });
        return new VmManager(settings, NullLogger<VmManager>.Instance);
    }

    [Fact]
    public async Task ReserveAsync_ReturnsFirstAvailable()
    {
        var mgr = MakeManager("vm1", "vm2");

        var vm = await mgr.ReserveAsync();

        Assert.NotNull(vm);
        Assert.Equal("vm1", vm!.Name);
        Assert.Equal(VmState.Reserved, mgr.GetState("vm1"));
    }

    [Fact]
    public async Task ReserveAsync_SkipsReservedVms()
    {
        var mgr = MakeManager("vm1", "vm2");

        var first = await mgr.ReserveAsync();
        var second = await mgr.ReserveAsync();

        Assert.Equal("vm1", first!.Name);
        Assert.Equal("vm2", second!.Name);
    }

    [Fact]
    public async Task ReserveAsync_ReturnsNull_WhenAllTaken()
    {
        var mgr = MakeManager("vm1");

        await mgr.ReserveAsync();
        var second = await mgr.ReserveAsync();

        Assert.Null(second);
    }

    [Fact]
    public async Task ReleaseAsync_MakesVmAvailable()
    {
        var mgr = MakeManager("vm1");

        await mgr.ReserveAsync();
        Assert.Equal(VmState.Reserved, mgr.GetState("vm1"));

        await mgr.ReleaseAsync("vm1");
        Assert.Equal(VmState.Available, mgr.GetState("vm1"));
    }

    [Fact]
    public async Task ReleaseAsync_AllowsReReservation()
    {
        var mgr = MakeManager("vm1");

        await mgr.ReserveAsync();
        await mgr.ReleaseAsync("vm1");

        var vm = await mgr.ReserveAsync();
        Assert.NotNull(vm);
        Assert.Equal("vm1", vm!.Name);
    }

    [Fact]
    public async Task MarkBusyAsync_SetsState()
    {
        var mgr = MakeManager("vm1");

        await mgr.MarkBusyAsync("vm1");

        Assert.Equal(VmState.Busy, mgr.GetState("vm1"));
    }

    [Fact]
    public async Task MarkErrorAsync_SetsState()
    {
        var mgr = MakeManager("vm1");

        await mgr.MarkErrorAsync("vm1", "disk full");

        Assert.Equal(VmState.Error, mgr.GetState("vm1"));
    }

    [Fact]
    public async Task BusyVm_NotReservable()
    {
        var mgr = MakeManager("vm1");

        await mgr.MarkBusyAsync("vm1");
        var vm = await mgr.ReserveAsync();

        Assert.Null(vm);
    }

    [Fact]
    public async Task ErrorVm_NotReservable()
    {
        var mgr = MakeManager("vm1");

        await mgr.MarkErrorAsync("vm1", "broken");
        var vm = await mgr.ReserveAsync();

        Assert.Null(vm);
    }

    [Fact]
    public void GetState_ThrowsOnUnknownVm()
    {
        var mgr = MakeManager("vm1");

        Assert.Throws<ArgumentException>(() => mgr.GetState("nonexistent"));
    }

    [Fact]
    public async Task ReleaseAsync_ThrowsOnUnknownVm()
    {
        var mgr = MakeManager("vm1");

        await Assert.ThrowsAsync<ArgumentException>(() => mgr.ReleaseAsync("nonexistent"));
    }

    [Fact]
    public void GetAllVms_ReturnsAllConfigured()
    {
        var mgr = MakeManager("vm1", "vm2", "vm3");

        var all = mgr.GetAllVms();

        Assert.Equal(3, all.Count);
    }
}
