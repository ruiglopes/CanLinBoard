// software/CanLinConfig.Tests/TracePanelViewModelTests.cs
using CanLinConfig.Models;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Tests;

public class TracePanelViewModelTests
{
    [Fact]
    public void AddFrame_adds_entry_to_list()
    {
        var vm = new TracePanelViewModel(maxEntries: 100);
        var frame = new BusFrame(BusFrame.Bus.CAN1, 0x100, 8, new byte[8], DateTime.Now);
        vm.AddFrame(frame, messageName: "EngineData");
        Assert.Single(vm.Entries);
        Assert.Equal("0x100", vm.Entries[0].Id);
        Assert.Equal("CAN1", vm.Entries[0].Bus);
        Assert.Equal("EngineData", vm.Entries[0].MessageName);
    }

    [Fact]
    public void AddFrame_caps_at_max_entries()
    {
        var vm = new TracePanelViewModel(maxEntries: 5);
        for (int i = 0; i < 10; i++)
            vm.AddFrame(new BusFrame(BusFrame.Bus.CAN1, (uint)i, 0, new byte[8], DateTime.Now), null);
        Assert.Equal(5, vm.Entries.Count);
    }

    [Fact]
    public void Paused_state_prevents_adds()
    {
        var vm = new TracePanelViewModel(maxEntries: 100);
        vm.IsPaused = true;
        vm.AddFrame(new BusFrame(BusFrame.Bus.CAN1, 0x100, 0, new byte[8], DateTime.Now), null);
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void Clear_removes_all_entries()
    {
        var vm = new TracePanelViewModel(maxEntries: 100);
        vm.AddFrame(new BusFrame(BusFrame.Bus.CAN1, 0x100, 0, new byte[8], DateTime.Now), null);
        vm.AddFrame(new BusFrame(BusFrame.Bus.CAN1, 0x101, 0, new byte[8], DateTime.Now), null);
        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.Entries);
    }
}
