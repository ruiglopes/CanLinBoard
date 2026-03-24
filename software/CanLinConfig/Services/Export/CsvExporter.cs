using System.Globalization;
using System.IO;
using System.Text;
using CanLinConfig.Models;

namespace CanLinConfig.Services.Export;

public class CsvExporter : IFrameExporter
{
    public string FileExtension => ".csv";
    public string FileFilter => "CSV Files (*.csv)|*.csv";

    public void Export(Stream stream, IReadOnlyList<BusFrame> frames, DatabaseManager? dbManager = null)
    {
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);

        var signalNames = new List<string>();
        if (dbManager != null)
        {
            var seen = new HashSet<string>();
            foreach (var f in frames)
            {
                var signals = dbManager.GetSignals(f.SourceBus, f.Id);
                foreach (var s in signals)
                    if (seen.Add(s.Name))
                        signalNames.Add(s.Name);
            }
        }

        var header = "timestamp_ms,bus,id,dlc,data,message";
        if (signalNames.Count > 0)
            header += "," + string.Join(",", signalNames);
        writer.WriteLine(header);

        foreach (var f in frames)
        {
            var dataHex = string.Join(" ", f.Data.Take(f.Dlc).Select(b => b.ToString("X2")));
            var msgName = dbManager?.GetMessageName(f.SourceBus, f.Id) ?? "";
            var line = string.Format(CultureInfo.InvariantCulture,
                "{0:F3},{1},0x{2:X3},{3},{4},{5}",
                (f.Timestamp - DateTime.UnixEpoch).TotalMilliseconds,
                f.BusName, f.Id, f.Dlc, dataHex, msgName);

            if (signalNames.Count > 0)
            {
                var decoded = dbManager!.DecodeFrame(f);
                var valueMap = decoded.ToDictionary(v => v.Name, v => v.PhysicalValue);
                foreach (var name in signalNames)
                {
                    line += ",";
                    if (valueMap.TryGetValue(name, out var val))
                        line += val.ToString(CultureInfo.InvariantCulture);
                }
            }
            writer.WriteLine(line);
        }
    }
}
