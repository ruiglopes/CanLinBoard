using CanLinConfig.Models;
using CanLinConfig.Services;
using CanLinConfig.Services.Export;

namespace CanLinConfig.Tests;

public class ExportIntegrationTests
{
    private static readonly DateTime T0 = new(2026, 3, 23, 10, 0, 0, 0, DateTimeKind.Utc);

    private static List<BusFrame> MakeTestFrames()
    {
        return
        [
            new(BusFrame.Bus.CAN1, 0x100, 3, [0x01, 0x02, 0x03, 0, 0, 0, 0, 0], T0),
            new(BusFrame.Bus.CAN2, 0x200, 2, [0xAB, 0xCD, 0, 0, 0, 0, 0, 0], T0.AddMilliseconds(100)),
            new(BusFrame.Bus.LIN1, 33, 4, [25, 200, 0, 0, 0, 0, 0, 0], T0.AddMilliseconds(200)),
        ];
    }

    [Theory]
    [InlineData(typeof(CsvExporter))]
    [InlineData(typeof(AscExporter))]
    [InlineData(typeof(BlfExporter))]
    public void AllExporters_handle_mixed_bus_frames(Type exporterType)
    {
        var exporter = (IFrameExporter)Activator.CreateInstance(exporterType)!;
        using var ms = new MemoryStream();
        exporter.Export(ms, MakeTestFrames());
        Assert.True(ms.Length > 0);
    }

    [Fact]
    public void Csv_with_ldf_signals_decodes_lin_frames()
    {
        var dbMgr = new DatabaseManager();
        var testLdf = Path.Combine(AppContext.BaseDirectory, "TestData", "test.ldf");
        dbMgr.AssignDatabase(BusFrame.Bus.LIN1, testLdf);

        var exporter = new CsvExporter();
        var frames = new List<BusFrame>
        {
            new(BusFrame.Bus.LIN1, 33, 4, [25, 200, 0, 0, 0, 0, 0, 0], T0),
        };

        using var ms = new MemoryStream();
        exporter.Export(ms, frames, dbMgr);
        var csv = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        Assert.Contains("MotorSpeed", csv);
        Assert.Contains("2500", csv);
        Assert.Contains("160", csv);
    }
}
