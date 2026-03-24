using System.Globalization;
using System.IO;
using System.Text;
using CanLinConfig.Models;

namespace CanLinConfig.Services;

public static class CsvLogImporter
{
    public static List<BusFrame> Import(string csvText)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csvText));
        return ImportFromStream(stream);
    }

    public static List<BusFrame> ImportFromStream(Stream stream)
    {
        var frames = new List<BusFrame>();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        // Skip header
        var header = reader.ReadLine();
        if (header == null) return frames;

        while (reader.ReadLine() is { } line)
        {
            try
            {
                var frame = ParseLine(line);
                if (frame != null)
                    frames.Add(frame);
            }
            catch
            {
                // Skip malformed lines
            }
        }

        return frames;
    }

    private static BusFrame? ParseLine(string line)
    {
        // Format: timestamp_ms,bus,id,dlc,data,message
        var parts = line.Split(',', 6);
        if (parts.Length < 5) return null;

        // Timestamp can be integer (e.g. "1000") or float (e.g. "1711180800000.000")
        if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double timestampMs))
            return null;

        var bus = ParseBus(parts[1].Trim());
        if (bus == BusFrame.Bus.Unknown) return null;

        var idStr = parts[2].Trim();
        uint id;
        if (idStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            id = uint.Parse(idStr[2..], NumberStyles.HexNumber);
        else
            id = uint.Parse(idStr);

        byte dlc = byte.Parse(parts[3].Trim());

        var data = new byte[8];
        var dataStr = parts[4].Trim();
        if (!string.IsNullOrEmpty(dataStr))
        {
            var bytes = dataStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < bytes.Length && i < 8; i++)
                data[i] = byte.Parse(bytes[i], NumberStyles.HexNumber);
        }

        // Reconstruct timestamp: if value is a large epoch-ms value, use it as-is;
        // otherwise treat as a simple ms offset from a base time
        DateTime timestamp = DateTime.UnixEpoch.AddMilliseconds(timestampMs);

        return new BusFrame(bus, id, dlc, data, timestamp, false);
    }

    private static BusFrame.Bus ParseBus(string name) => name.ToUpperInvariant() switch
    {
        "CAN1" => BusFrame.Bus.CAN1,
        "CAN2" => BusFrame.Bus.CAN2,
        "LIN1" => BusFrame.Bus.LIN1,
        "LIN2" => BusFrame.Bus.LIN2,
        "LIN3" => BusFrame.Bus.LIN3,
        "LIN4" => BusFrame.Bus.LIN4,
        _ => BusFrame.Bus.Unknown
    };
}
