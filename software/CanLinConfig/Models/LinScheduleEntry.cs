using CommunityToolkit.Mvvm.ComponentModel;

namespace CanLinConfig.Models;

public partial class LinScheduleEntry : ObservableObject
{
    [ObservableProperty] private byte _id;
    [ObservableProperty] private byte _dlc = 8;
    [ObservableProperty] private byte _direction; // 0=subscribe, 1=publish
    [ObservableProperty] private byte[] _data = new byte[8];
    [ObservableProperty] private ushort _delayMs = 10;
    [ObservableProperty] private bool _classicChecksum;

    public string DirectionText => Direction == 0 ? "Subscribe" : "Publish";
    public string DataHex
    {
        get => string.Join(" ", Data.Take(Dlc).Select(b => b.ToString("X2")));
        set
        {
            var bytes = new byte[8];
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < Math.Min(parts.Length, 8); i++)
                if (byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out byte b))
                    bytes[i] = b;
            Data = bytes;
            OnPropertyChanged();
        }
    }

    // Packed size matching firmware lin_schedule_entry_t
    public const int PackedSize = 14;

    public byte[] Serialize()
    {
        var buf = new byte[PackedSize];
        buf[0] = Id;
        buf[1] = Dlc;
        buf[2] = Direction;
        Array.Copy(Data, 0, buf, 3, 8);
        buf[11] = (byte)DelayMs;
        buf[12] = (byte)(DelayMs >> 8);
        buf[13] = (byte)(ClassicChecksum ? 1 : 0);
        return buf;
    }

    public static LinScheduleEntry Deserialize(byte[] buf, int offset)
    {
        var e = new LinScheduleEntry
        {
            Id = buf[offset],
            Dlc = buf[offset + 1],
            Direction = buf[offset + 2],
            DelayMs = (ushort)(buf[offset + 11] | (buf[offset + 12] << 8)),
            ClassicChecksum = buf[offset + 13] != 0,
        };
        Array.Copy(buf, offset + 3, e.Data, 0, 8);
        return e;
    }
}
