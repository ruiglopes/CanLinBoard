using CanLinConfig.Models;
using CanLinConfig.Protocol;

namespace CanLinConfig.Tests;

public class LinScheduleEntryTests
{
    [Fact]
    public void Serialize_produces_expected_size()
    {
        var entry = new LinScheduleEntry { Id = 33, Dlc = 4, Direction = 1, DelayMs = 10 };
        var bytes = entry.Serialize();
        Assert.Equal(ProtocolConstants.ExpectedLinEntrySize, bytes.Length);
    }

    [Fact]
    public void Serialize_Deserialize_round_trips()
    {
        var entry = new LinScheduleEntry
        {
            Id = 0x21, Dlc = 8, Direction = 1,
            Data = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08],
            DelayMs = 500, ClassicChecksum = true
        };

        var bytes = entry.Serialize();
        var restored = LinScheduleEntry.Deserialize(bytes, 0);

        Assert.Equal(0x21, restored.Id);
        Assert.Equal(8, restored.Dlc);
        Assert.Equal(1, restored.Direction);
        Assert.Equal(entry.Data, restored.Data);
        Assert.Equal(500, restored.DelayMs);
        Assert.True(restored.ClassicChecksum);
    }

    [Fact]
    public void DataHex_get_formats_correctly()
    {
        var entry = new LinScheduleEntry { Dlc = 3, Data = [0xAB, 0xCD, 0xEF, 0, 0, 0, 0, 0] };
        Assert.Equal("AB CD EF", entry.DataHex);
    }

    [Fact]
    public void DataHex_set_parses_hex()
    {
        var entry = new LinScheduleEntry();
        entry.DataHex = "01 FF A0";
        Assert.Equal(0x01, entry.Data[0]);
        Assert.Equal(0xFF, entry.Data[1]);
        Assert.Equal(0xA0, entry.Data[2]);
    }
}
