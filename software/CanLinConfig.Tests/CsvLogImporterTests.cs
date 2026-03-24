using CanLinConfig.Models;
using CanLinConfig.Services;

namespace CanLinConfig.Tests;

public class CsvLogImporterTests
{
    [Fact]
    public void Import_parses_standard_csv()
    {
        var csv = "timestamp_ms,bus,id,dlc,data,message\n" +
                  "1000,CAN1,0x100,3,AA BB CC,\n" +
                  "2000,CAN2,0x200,2,11 22,\n";

        var frames = CsvLogImporter.Import(csv);

        Assert.Equal(2, frames.Count);
        Assert.Equal(BusFrame.Bus.CAN1, frames[0].SourceBus);
        Assert.Equal(0x100u, frames[0].Id);
        Assert.Equal(3, frames[0].Dlc);
        Assert.Equal(0xAA, frames[0].Data[0]);
        Assert.Equal(BusFrame.Bus.CAN2, frames[1].SourceBus);
        Assert.Equal(0x200u, frames[1].Id);
    }

    [Fact]
    public void Import_parses_LIN_buses()
    {
        var csv = "timestamp_ms,bus,id,dlc,data,message\n" +
                  "0,LIN1,0x3C,8,01 02 03 04 05 06 07 08,\n" +
                  "100,LIN4,0x1A,2,FF 00,\n";

        var frames = CsvLogImporter.Import(csv);

        Assert.Equal(2, frames.Count);
        Assert.Equal(BusFrame.Bus.LIN1, frames[0].SourceBus);
        Assert.Equal(BusFrame.Bus.LIN4, frames[1].SourceBus);
    }

    [Fact]
    public void Import_handles_empty_file()
    {
        var frames = CsvLogImporter.Import("timestamp_ms,bus,id,dlc,data,message\n");
        Assert.Empty(frames);
    }

    [Fact]
    public void Import_skips_malformed_lines()
    {
        var csv = "timestamp_ms,bus,id,dlc,data,message\n" +
                  "bad,line\n" +
                  "1000,CAN1,0x100,2,AA BB,\n";

        var frames = CsvLogImporter.Import(csv);
        Assert.Single(frames);
    }

    [Fact]
    public void Import_from_stream()
    {
        var csv = "timestamp_ms,bus,id,dlc,data,message\n" +
                  "500,CAN1,0x100,1,FF,\n";

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));
        var frames = CsvLogImporter.ImportFromStream(stream);

        Assert.Single(frames);
        Assert.Equal(0xFFu, frames[0].Data[0]);
    }
}
