// software/CanLinConfig.Tests/BusDataServiceTests.cs
using CanLinConfig.Models;
using CanLinConfig.Services;

namespace CanLinConfig.Tests;

public class BusDataServiceTests
{
    [Fact]
    public void OnFrame_raises_FrameReceived_event()
    {
        var dbMgr = new DatabaseManager();
        var svc = new BusDataService(dbMgr);
        BusFrame? received = null;
        svc.FrameReceived += (_, f) => received = f;

        var frame = new BusFrame(BusFrame.Bus.CAN1, 0x100, 8, new byte[8], DateTime.Now);
        svc.OnFrame(frame);

        Assert.NotNull(received);
        Assert.Equal(0x100u, received!.Id);
        Assert.Equal(BusFrame.Bus.CAN1, received.SourceBus);
    }

    [Fact]
    public void OnFrame_raises_SignalsDecoded_when_database_loaded()
    {
        var dbMgr = new DatabaseManager();
        var testDbc = Path.Combine(AppContext.BaseDirectory, "TestData", "test.dbc");
        dbMgr.AssignDatabase(BusFrame.Bus.CAN1, testDbc);

        var svc = new BusDataService(dbMgr);
        IReadOnlyList<SignalValue>? decoded = null;
        svc.SignalsDecoded += (_, signals) => decoded = signals;

        byte[] data = [0xE8, 0x03, 200, 0x01, 0, 0, 0, 0];
        var frame = new BusFrame(BusFrame.Bus.CAN1, 256, 8, data, DateTime.Now);
        svc.OnFrame(frame);

        Assert.NotNull(decoded);
        Assert.Equal(3, decoded!.Count);
    }

    [Fact]
    public void OnFrame_no_signals_for_unknown_message()
    {
        var dbMgr = new DatabaseManager();
        var svc = new BusDataService(dbMgr);
        bool signalFired = false;
        svc.SignalsDecoded += (_, _) => signalFired = true;

        var frame = new BusFrame(BusFrame.Bus.CAN1, 0x999, 8, new byte[8], DateTime.Now);
        svc.OnFrame(frame);

        Assert.False(signalFired);
    }

    [Fact]
    public void OnCanFrame_wraps_with_CAN1_bus()
    {
        var dbMgr = new DatabaseManager();
        var svc = new BusDataService(dbMgr);
        BusFrame? received = null;
        svc.FrameReceived += (_, f) => received = f;

        var canFrame = new CanLinConfig.Adapters.CanFrame(0x100, new byte[8]);
        svc.OnCanFrame(canFrame);

        Assert.NotNull(received);
        Assert.Equal(BusFrame.Bus.CAN1, received!.SourceBus);
    }

    [Fact]
    public void History_buffer_stores_frames()
    {
        var dbMgr = new DatabaseManager();
        var svc = new BusDataService(dbMgr, maxHistory: 100);

        for (int i = 0; i < 10; i++)
            svc.OnFrame(new BusFrame(BusFrame.Bus.CAN1, (uint)i, 0, new byte[8], DateTime.Now));

        Assert.Equal(10, svc.FrameHistory.Count);
    }

    [Fact]
    public void History_buffer_caps_at_max()
    {
        var dbMgr = new DatabaseManager();
        var svc = new BusDataService(dbMgr, maxHistory: 5);

        for (int i = 0; i < 10; i++)
            svc.OnFrame(new BusFrame(BusFrame.Bus.CAN1, (uint)i, 0, new byte[8], DateTime.Now));

        Assert.Equal(5, svc.FrameHistory.Count);
        Assert.Equal(5u, svc.FrameHistory[0].Id);
    }
}
