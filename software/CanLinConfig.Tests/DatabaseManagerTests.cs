using CanLinConfig.Models;
using CanLinConfig.Services;

namespace CanLinConfig.Tests;

public class DatabaseManagerTests
{
    private static string TestDbcPath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "test.dbc");

    [Fact]
    public void AssignDatabase_loads_and_caches_signals()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.CAN1, TestDbcPath);
        var signals = mgr.GetSignals(BusFrame.Bus.CAN1, 256);
        Assert.Equal(3, signals.Count);
        Assert.Contains(signals, s => s.Name == "EngineRPM");
        Assert.Contains(signals, s => s.Name == "CoolantTemp");
        Assert.Contains(signals, s => s.Name == "EngineOn");
    }

    [Fact]
    public void GetSignals_returns_empty_for_unknown_bus()
    {
        var mgr = new DatabaseManager();
        var signals = mgr.GetSignals(BusFrame.Bus.CAN2, 256);
        Assert.Empty(signals);
    }

    [Fact]
    public void GetSignals_returns_empty_for_unknown_id()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.CAN1, TestDbcPath);
        var signals = mgr.GetSignals(BusFrame.Bus.CAN1, 999);
        Assert.Empty(signals);
    }

    [Fact]
    public void GetMessageName_returns_name_when_loaded()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.CAN1, TestDbcPath);
        Assert.Equal("EngineData", mgr.GetMessageName(BusFrame.Bus.CAN1, 256));
        Assert.Null(mgr.GetMessageName(BusFrame.Bus.CAN1, 999));
        Assert.Null(mgr.GetMessageName(BusFrame.Bus.CAN2, 256));
    }

    [Fact]
    public void RemoveDatabase_clears_assignment()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.CAN1, TestDbcPath);
        mgr.RemoveDatabase(BusFrame.Bus.CAN1);
        Assert.Empty(mgr.GetSignals(BusFrame.Bus.CAN1, 256));
    }

    [Fact]
    public void DecodeFrame_extracts_physical_values()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.CAN1, TestDbcPath);

        byte[] data = [0xE8, 0x03, 200, 0x01, 0, 0, 0, 0];
        // RPM raw = 0x03E8 = 1000, physical = 1000 * 0.25 = 250 rpm
        // Temp raw = 200, physical = 200 - 40 = 160 C
        // EngineOn raw = 1

        var frame = new BusFrame(BusFrame.Bus.CAN1, 256, 8, data, DateTime.Now);
        var values = mgr.DecodeFrame(frame);

        Assert.Equal(3, values.Count);

        var rpm = values.First(v => v.Name == "EngineRPM");
        Assert.Equal(250.0, rpm.PhysicalValue);
        Assert.Equal("rpm", rpm.Unit);

        var temp = values.First(v => v.Name == "CoolantTemp");
        Assert.Equal(160.0, temp.PhysicalValue);

        var eng = values.First(v => v.Name == "EngineOn");
        Assert.Equal(1.0, eng.RawValue);
    }
}
