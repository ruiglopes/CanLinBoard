using CanLinConfig.Models;
using CanLinConfig.Services;

namespace CanLinConfig.Tests;

public class LdfIntegrationTests
{
    private static string TestLdfPath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "test.ldf");

    [Fact]
    public void AssignDatabase_ldf_loads_frames_as_messages()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.LIN1, TestLdfPath);
        Assert.Equal("MotorStatus", mgr.GetMessageName(BusFrame.Bus.LIN1, 33));
        Assert.Equal("MotorControl", mgr.GetMessageName(BusFrame.Bus.LIN1, 34));
        Assert.Null(mgr.GetMessageName(BusFrame.Bus.LIN1, 99));
    }

    [Fact]
    public void AssignDatabase_ldf_converts_signals()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.LIN1, TestLdfPath);
        var signals = mgr.GetSignals(BusFrame.Bus.LIN1, 33);
        Assert.Equal(2, signals.Count);
        Assert.Contains(signals, s => s.Name == "MotorSpeed");
        Assert.Contains(signals, s => s.Name == "MotorTemp");
        var speed = signals.First(s => s.Name == "MotorSpeed");
        Assert.Equal(0, speed.StartBit);
        Assert.Equal(8, speed.BitLength);
        Assert.Equal(100.0, speed.Factor);
        Assert.Equal(0.0, speed.Offset);
        Assert.Equal("rpm", speed.Unit);
    }

    [Fact]
    public void DecodeFrame_ldf_extracts_physical_values()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.LIN1, TestLdfPath);
        byte[] data = [25, 200, 0, 0, 0, 0, 0, 0];
        var frame = new BusFrame(BusFrame.Bus.LIN1, 33, 4, data, DateTime.Now);
        var values = mgr.DecodeFrame(frame);
        Assert.Equal(2, values.Count);
        var speed = values.First(v => v.Name == "MotorSpeed");
        Assert.Equal(2500.0, speed.PhysicalValue);
        Assert.Equal("rpm", speed.Unit);
        var temp = values.First(v => v.Name == "MotorTemp");
        Assert.Equal(160.0, temp.PhysicalValue);
    }

    [Fact]
    public void AssignDatabase_autodetects_dbc_vs_ldf()
    {
        var mgr = new DatabaseManager();
        var testDbc = Path.Combine(AppContext.BaseDirectory, "TestData", "test.dbc");
        mgr.AssignDatabase(BusFrame.Bus.CAN1, testDbc);
        Assert.Equal("EngineData", mgr.GetMessageName(BusFrame.Bus.CAN1, 256));
        mgr.AssignDatabase(BusFrame.Bus.LIN1, TestLdfPath);
        Assert.Equal("MotorStatus", mgr.GetMessageName(BusFrame.Bus.LIN1, 33));
    }

    [Fact]
    public void RemoveDatabase_ldf_clears_cache()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.LIN1, TestLdfPath);
        mgr.RemoveDatabase(BusFrame.Bus.LIN1);
        Assert.Empty(mgr.GetSignals(BusFrame.Bus.LIN1, 33));
        Assert.Null(mgr.GetMessageName(BusFrame.Bus.LIN1, 33));
    }

    [Fact]
    public void Signal_without_physical_encoding_gets_defaults()
    {
        var mgr = new DatabaseManager();
        mgr.AssignDatabase(BusFrame.Bus.LIN1, TestLdfPath);
        var signals = mgr.GetSignals(BusFrame.Bus.LIN1, 34);
        Assert.Single(signals);
        var cmd = signals[0];
        Assert.Equal("MotorCommand", cmd.Name);
        Assert.Equal(1.0, cmd.Factor);
        Assert.Equal(0.0, cmd.Offset);
    }
}
