using CanLinConfig.Parsers;

namespace CanLinConfig.Tests;

public class LdfParserTests
{
    private static string TestLdfPath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "test.ldf");

    [Fact]
    public void Parse_header()
    {
        var ldf = LdfParser.Parse(TestLdfPath);
        Assert.Equal("2.1", ldf.ProtocolVersion);
        Assert.Equal(19.2, ldf.SpeedKbps);
        Assert.Equal(19200u, ldf.BaudRate);
    }

    [Fact]
    public void Parse_nodes()
    {
        var ldf = LdfParser.Parse(TestLdfPath);
        Assert.Equal("ECU_Master", ldf.MasterNode);
        Assert.Contains("Motor1", ldf.SlaveNodes);
    }

    [Fact]
    public void Parse_signals()
    {
        var ldf = LdfParser.Parse(TestLdfPath);
        Assert.Equal(3, ldf.Signals.Count);
        var speed = ldf.Signals.First(s => s.Name == "MotorSpeed");
        Assert.Equal(8, speed.BitSize);
        Assert.Equal("Motor1", speed.Publisher);
    }

    [Fact]
    public void Parse_frames()
    {
        var ldf = LdfParser.Parse(TestLdfPath);
        Assert.Equal(2, ldf.Frames.Count);
        var status = ldf.Frames.First(f => f.Name == "MotorStatus");
        Assert.Equal(33, status.Id);
        Assert.Equal(4, status.Size);
        Assert.Equal(2, status.Signals.Count);
    }

    [Fact]
    public void Parse_schedule_tables()
    {
        var ldf = LdfParser.Parse(TestLdfPath);
        Assert.Single(ldf.ScheduleTables);
        Assert.Equal("MainSchedule", ldf.ScheduleTables[0].Name);
        Assert.Equal(2, ldf.ScheduleTables[0].Entries.Count);
    }

    [Fact]
    public void Parse_signal_encodings()
    {
        var ldf = LdfParser.Parse(TestLdfPath);
        var speedEnc = ldf.SignalEncodings.First(e => e.Name == "SpeedEncoding");
        var phys = speedEnc.Values.First(v => v.IsPhysical);
        Assert.Equal(100.0, phys.Factor);
        Assert.Equal(0.0, phys.Offset);
        Assert.Equal("rpm", phys.Description);
    }

    [Fact]
    public void GetEncodingForSignal_resolves()
    {
        var ldf = LdfParser.Parse(TestLdfPath);
        var enc = LdfParser.GetEncodingForSignal(ldf, "MotorTemp");
        Assert.NotNull(enc);
        Assert.Equal("TempEncoding", enc!.Name);
    }

    [Fact]
    public void GetEncodingForSignal_returns_null_for_unknown()
    {
        var ldf = LdfParser.Parse(TestLdfPath);
        Assert.Null(LdfParser.GetEncodingForSignal(ldf, "NonExistent"));
    }
}
