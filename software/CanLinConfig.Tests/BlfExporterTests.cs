using CanLinConfig.Models;
using CanLinConfig.Services.Export;

namespace CanLinConfig.Tests;

public class BlfExporterTests
{
    private static readonly DateTime T0 = new(2026, 3, 23, 10, 0, 0, 0, DateTimeKind.Utc);

    private static BusFrame MakeFrame(uint id, byte[] data, BusFrame.Bus bus = BusFrame.Bus.CAN1, int offsetMs = 0)
        => new(bus, id, (byte)data.Length, data, T0.AddMilliseconds(offsetMs));

    [Fact]
    public void Export_writes_valid_blf_signature()
    {
        var exporter = new BlfExporter();
        using var ms = new MemoryStream();
        exporter.Export(ms, new List<BusFrame> { MakeFrame(0x100, [0xAB]) });
        var bytes = ms.ToArray();
        Assert.True(bytes.Length > 8);
        var sig = System.Text.Encoding.ASCII.GetString(bytes, 0, 7);
        Assert.Equal("BLF0400", sig);
    }

    [Fact]
    public void Export_creates_nonzero_file()
    {
        var exporter = new BlfExporter();
        using var ms = new MemoryStream();
        exporter.Export(ms, new List<BusFrame>
        {
            MakeFrame(0x100, [0x01, 0x02, 0x03]),
            MakeFrame(0x200, [0x04, 0x05], offsetMs: 100),
        });
        Assert.True(ms.Length > 144);
    }

    [Fact]
    public void Export_multiple_frames_increases_size()
    {
        var exporter = new BlfExporter();
        using var ms1 = new MemoryStream();
        exporter.Export(ms1, new List<BusFrame> { MakeFrame(0x100, [0x01]) });
        using var ms10 = new MemoryStream();
        exporter.Export(ms10, Enumerable.Range(0, 10).Select(i => MakeFrame(0x100, [0x01], offsetMs: i * 10)).ToList());
        Assert.True(ms10.Length > ms1.Length);
    }

    [Fact]
    public void FileExtension_is_blf()
    {
        Assert.Equal(".blf", new BlfExporter().FileExtension);
    }
}
