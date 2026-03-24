using CanLinConfig.Parsers;

namespace CanLinConfig.Tests;

public class DbcParserTests
{
    private static string TestDbcPath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "test.dbc");

    [Fact]
    public void Parse_loads_messages()
    {
        var dbc = DbcParser.Parse(TestDbcPath);
        Assert.Equal(2, dbc.Messages.Count);
    }

    [Fact]
    public void Parse_message_fields()
    {
        var dbc = DbcParser.Parse(TestDbcPath);
        var msg = dbc.Messages.First(m => m.Id == 256);
        Assert.Equal("EngineData", msg.Name);
        Assert.Equal(8, msg.Dlc);
        Assert.Equal("ECU1", msg.Sender);
        Assert.Equal(3, msg.Signals.Count);
    }

    [Fact]
    public void Parse_signal_fields()
    {
        var dbc = DbcParser.Parse(TestDbcPath);
        var msg = dbc.Messages.First(m => m.Id == 256);
        var rpm = msg.Signals.First(s => s.Name == "EngineRPM");

        Assert.Equal(0, rpm.StartBit);
        Assert.Equal(16, rpm.BitLength);
        Assert.True(rpm.IsLittleEndian);
        Assert.False(rpm.IsSigned);
        Assert.Equal(0.25, rpm.Factor);
        Assert.Equal(0.0, rpm.Offset);
        Assert.Equal("rpm", rpm.Unit);
    }

    [Fact]
    public void Parse_second_message()
    {
        var dbc = DbcParser.Parse(TestDbcPath);
        var msg = dbc.Messages.First(m => m.Id == 512);
        Assert.Equal("VehicleSpeed", msg.Name);
        Assert.Equal(4, msg.Dlc);
        Assert.Equal(2, msg.Signals.Count);

        var speed = msg.Signals.First(s => s.Name == "Speed");
        Assert.Equal(0.01, speed.Factor);
        Assert.Equal("km/h", speed.Unit);
    }

    [Fact]
    public void Parse_nonexistent_file_throws()
    {
        Assert.ThrowsAny<Exception>(() => DbcParser.Parse(@"C:\nonexistent\fake.dbc"));
    }
}
