using CanLinConfig.Models;
using CanLinConfig.Services.Export;

namespace CanLinConfig.Tests;

public class AscExporterTests
{
    private static readonly DateTime T0 = new(2026, 3, 23, 10, 0, 0, 0, DateTimeKind.Utc);

    private static BusFrame MakeFrame(uint id, byte[] data, BusFrame.Bus bus = BusFrame.Bus.CAN1, int offsetMs = 0)
        => new(bus, id, (byte)data.Length, data, T0.AddMilliseconds(offsetMs));

    [Fact]
    public void Export_writes_asc_header()
    {
        var exporter = new AscExporter();
        using var ms = new MemoryStream();
        exporter.Export(ms, new List<BusFrame> { MakeFrame(0x100, [0xAB]) });
        var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        Assert.StartsWith("date", text);
        Assert.Contains("base hex", text);
        Assert.Contains("timestamps absolute", text);
    }

    [Fact]
    public void Export_writes_can_frame_line()
    {
        var exporter = new AscExporter();
        using var ms = new MemoryStream();
        exporter.Export(ms, new List<BusFrame> { MakeFrame(0x123, [0x01, 0x02, 0x03], offsetMs: 1234) });
        var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var dataLine = lines.Last();
        Assert.Contains("123", dataLine);
        Assert.Contains("Rx", dataLine);
        Assert.Contains("3", dataLine);
        Assert.Contains("01", dataLine);
    }

    [Fact]
    public void Export_maps_bus_to_channel()
    {
        var exporter = new AscExporter();
        var frames = new List<BusFrame>
        {
            MakeFrame(0x100, [0x01], BusFrame.Bus.CAN1),
            MakeFrame(0x200, [0x02], BusFrame.Bus.CAN2),
            MakeFrame(0x33, [0x03], BusFrame.Bus.LIN1),
        };
        using var ms = new MemoryStream();
        exporter.Export(ms, frames);
        var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var dataLines = lines.Where(l => !l.StartsWith("date") && !l.StartsWith("base") && !l.StartsWith("no") && !l.StartsWith("timestamps")).ToArray();
        Assert.Equal(3, dataLines.Length);
    }

    [Fact]
    public void FileExtension_is_asc()
    {
        Assert.Equal(".asc", new AscExporter().FileExtension);
    }
}
