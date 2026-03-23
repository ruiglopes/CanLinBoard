// software/CanLinConfig.Tests/IntegrationTests.cs
using CanLinConfig.Models;
using CanLinConfig.Services;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Tests;

public class IntegrationTests
{
    [Fact]
    public void EndToEnd_frame_flows_through_pipeline()
    {
        var dbManager = new DatabaseManager();
        var testDbc = Path.Combine(AppContext.BaseDirectory, "TestData", "test.dbc");
        dbManager.AssignDatabase(BusFrame.Bus.CAN1, testDbc);

        var busData = new BusDataService(dbManager);
        var trace = new TracePanelViewModel();
        var signals = new SignalPanelViewModel();

        busData.FrameReceived += (_, f) =>
            trace.AddFrame(f, dbManager.GetMessageName(f.SourceBus, f.Id));
        busData.SignalsDecoded += (_, sv) =>
            signals.UpdateSignals(sv);

        // EngineData (ID=256): RPM raw=1000, Temp raw=200, EngineOn=1
        byte[] data = [0xE8, 0x03, 200, 0x01, 0, 0, 0, 0];
        var frame = new CanLinConfig.Adapters.CanFrame(256, data);

        busData.OnCanFrame(frame);

        Assert.Single(trace.Entries);
        Assert.Equal("EngineData", trace.Entries[0].MessageName);
        Assert.Equal("CAN1", trace.Entries[0].Bus);

        Assert.Equal(3, signals.Signals.Count);
        var rpm = signals.Signals.First(s => s.Name == "EngineRPM");
        Assert.Equal(250.0, rpm.Value); // 1000 * 0.25
        Assert.Equal("rpm", rpm.Unit);
    }

    [Fact]
    public void EndToEnd_unknown_message_still_shows_in_trace()
    {
        var dbManager = new DatabaseManager();
        var busData = new BusDataService(dbManager);
        var trace = new TracePanelViewModel();

        busData.FrameReceived += (_, f) =>
            trace.AddFrame(f, dbManager.GetMessageName(f.SourceBus, f.Id));

        var frame = new CanLinConfig.Adapters.CanFrame(0x7FF, new byte[] { 0x01, 0x02 }, dlc: 2);
        busData.OnCanFrame(frame);

        Assert.Single(trace.Entries);
        Assert.Null(trace.Entries[0].MessageName);
    }
}
