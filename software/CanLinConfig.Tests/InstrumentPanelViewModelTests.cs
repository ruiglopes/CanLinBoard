using CanLinConfig.Models;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Tests;

public class InstrumentPanelViewModelTests
{
    [Fact]
    public void AddWidget_adds_to_collection()
    {
        var vm = new InstrumentPanelViewModel();
        vm.AddWidget("0:256:EngineRPM", "EngineRPM", "rpm", WidgetType.Numeric, 0, 8000);
        Assert.Single(vm.Widgets);
        Assert.Equal("EngineRPM", vm.Widgets[0].SignalName);
        Assert.Equal(WidgetType.Numeric, vm.Widgets[0].Type);
    }

    [Fact]
    public void AddWidget_prevents_duplicate_signal()
    {
        var vm = new InstrumentPanelViewModel();
        vm.AddWidget("0:256:EngineRPM", "EngineRPM", "rpm", WidgetType.Numeric, 0, 8000);
        vm.AddWidget("0:256:EngineRPM", "EngineRPM", "rpm", WidgetType.Bar, 0, 8000);
        Assert.Single(vm.Widgets);
    }

    [Fact]
    public void RemoveWidget_removes_from_collection()
    {
        var vm = new InstrumentPanelViewModel();
        vm.AddWidget("0:256:EngineRPM", "EngineRPM", "rpm", WidgetType.Numeric, 0, 8000);
        vm.RemoveWidgetCommand.Execute(vm.Widgets[0]);
        Assert.Empty(vm.Widgets);
    }

    [Fact]
    public void OnSignalValues_updates_matching_widget()
    {
        var vm = new InstrumentPanelViewModel();
        vm.AddWidget("0:256:EngineRPM", "EngineRPM", "rpm", WidgetType.Numeric, 0, 8000);
        var signals = new List<SignalValue>
        {
            new("EngineRPM", 1000, 250.0, "rpm", 0, 256, DateTime.Now),
            new("CoolantTemp", 200, 160.0, "C", 0, 256, DateTime.Now),
        };
        vm.OnSignalValues(signals);
        Assert.Equal(250.0, vm.Widgets[0].Value);
    }

    [Fact]
    public void OnSignalValues_ignores_unmatched_signals()
    {
        var vm = new InstrumentPanelViewModel();
        vm.AddWidget("0:256:EngineRPM", "EngineRPM", "rpm", WidgetType.Numeric, 0, 8000);
        var signals = new List<SignalValue>
        {
            new("CoolantTemp", 200, 160.0, "C", 0, 256, DateTime.Now),
        };
        vm.OnSignalValues(signals);
        Assert.Equal(0.0, vm.Widgets[0].Value);
    }

    [Fact]
    public void NormalizedValue_clamps_to_0_1()
    {
        var widget = new InstrumentWidget { RangeMin = 0, RangeMax = 100 };
        widget.UpdateValue(50);
        Assert.Equal(0.5, widget.NormalizedValue);
        widget.UpdateValue(150);
        Assert.Equal(1.0, widget.NormalizedValue);
        widget.UpdateValue(-10);
        Assert.Equal(0.0, widget.NormalizedValue);
    }

    [Fact]
    public void ToLayouts_and_FromLayouts_round_trip()
    {
        var vm = new InstrumentPanelViewModel();
        vm.AddWidget("0:256:EngineRPM", "EngineRPM", "rpm", WidgetType.Gauge, 0, 8000);
        vm.AddWidget("0:256:CoolantTemp", "CoolantTemp", "C", WidgetType.Bar, -40, 215);
        var layouts = vm.ToLayouts();
        Assert.Equal(2, layouts.Count);

        var vm2 = new InstrumentPanelViewModel();
        vm2.FromLayouts(layouts);
        Assert.Equal(2, vm2.Widgets.Count);
        Assert.Equal("EngineRPM", vm2.Widgets[0].SignalName);
        Assert.Equal(WidgetType.Gauge, vm2.Widgets[0].Type);
        Assert.Equal(8000, vm2.Widgets[0].RangeMax);
    }
}
