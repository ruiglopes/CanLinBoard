using CanLinConfig.Models;
using CanLinConfig.Services;
using CanLinConfig.Services.Export;

namespace CanLinConfig.Tests;

public class CsvExporterTests
{
    private static readonly DateTime T0 = new(2026, 3, 23, 10, 0, 0, DateTimeKind.Utc);

    private static BusFrame MakeFrame(uint id, byte[] data, BusFrame.Bus bus = BusFrame.Bus.CAN1, DateTime? ts = null)
        => new(bus, id, (byte)data.Length, data, ts ?? T0);

    [Fact]
    public void Export_writes_header_and_rows()
    {
        var exporter = new CsvExporter();
        var frames = new List<BusFrame>
        {
            MakeFrame(0x100, [0xAB, 0xCD]),
            MakeFrame(0x200, [0x01, 0x02, 0x03]),
        };
        using var ms = new MemoryStream();
        exporter.Export(ms, frames);
        var csv = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("timestamp_ms", lines[0]);
        Assert.Contains("0x100", lines[1]);
        Assert.Contains("AB CD", lines[1]);
        Assert.Contains("0x200", lines[2]);
    }

    [Fact]
    public void Export_with_database_adds_signal_columns()
    {
        var dbMgr = new DatabaseManager();
        var testDbc = Path.Combine(AppContext.BaseDirectory, "TestData", "test.dbc");
        dbMgr.AssignDatabase(BusFrame.Bus.CAN1, testDbc);
        var exporter = new CsvExporter();
        var frames = new List<BusFrame>
        {
            MakeFrame(256, [0xE8, 0x03, 200, 0x01, 0, 0, 0, 0]),
        };
        using var ms = new MemoryStream();
        exporter.Export(ms, frames, dbMgr);
        var csv = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("EngineRPM", lines[0]);
        Assert.Contains("CoolantTemp", lines[0]);
        Assert.Contains("250", lines[1]);
        Assert.Contains("160", lines[1]);
    }

    [Fact]
    public void Export_empty_frames_writes_header_only()
    {
        var exporter = new CsvExporter();
        using var ms = new MemoryStream();
        exporter.Export(ms, []);
        var csv = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
    }

    [Fact]
    public void FileExtension_is_csv()
    {
        Assert.Equal(".csv", new CsvExporter().FileExtension);
    }
}
