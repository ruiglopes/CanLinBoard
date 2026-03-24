using System.Globalization;
using System.IO;
using System.Text;
using CanLinConfig.Models;

namespace CanLinConfig.Services.Export;

public class AscExporter : IFrameExporter
{
    public string FileExtension => ".asc";
    public string FileFilter => "ASC Files (*.asc)|*.asc";

    public void Export(Stream stream, IReadOnlyList<BusFrame> frames, DatabaseManager? dbManager = null)
    {
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
        var startTime = frames.Count > 0 ? frames[0].Timestamp : DateTime.Now;
        writer.WriteLine($"date {startTime:ddd MMM dd hh:mm:ss tt yyyy}");
        writer.WriteLine("base hex  timestamps absolute");
        writer.WriteLine("no internal events logged");

        foreach (var f in frames)
        {
            var relTime = (f.Timestamp - startTime).TotalSeconds;
            var channel = BusToChannel(f.SourceBus);
            var dataBytes = string.Join(" ", f.Data.Take(f.Dlc).Select(b => b.ToString("x2")));
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "   {0:F6} {1}  {2:X}x             Rx   d {3} {4}",
                relTime, channel, f.Id, f.Dlc, dataBytes));
        }
    }

    private static int BusToChannel(BusFrame.Bus bus) => bus switch
    {
        BusFrame.Bus.CAN1 => 1, BusFrame.Bus.CAN2 => 2,
        BusFrame.Bus.LIN1 => 3, BusFrame.Bus.LIN2 => 4,
        BusFrame.Bus.LIN3 => 5, BusFrame.Bus.LIN4 => 6, _ => 1
    };
}
