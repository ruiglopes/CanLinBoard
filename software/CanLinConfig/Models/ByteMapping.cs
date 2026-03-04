using CommunityToolkit.Mvvm.ComponentModel;

namespace CanLinConfig.Models;

public partial class ByteMapping : ObservableObject
{
    [ObservableProperty] private byte _srcByte;
    [ObservableProperty] private byte _dstByte;
    [ObservableProperty] private byte _mask = 0xFF;
    [ObservableProperty] private sbyte _shift;
    [ObservableProperty] private sbyte _offset;

    public const int PackedSize = 5;

    public byte[] Serialize()
    {
        return [SrcByte, DstByte, Mask, (byte)Shift, (byte)Offset];
    }

    public static ByteMapping Deserialize(byte[] buf, int offset)
    {
        return new ByteMapping
        {
            SrcByte = buf[offset],
            DstByte = buf[offset + 1],
            Mask = buf[offset + 2],
            Shift = (sbyte)buf[offset + 3],
            Offset = (sbyte)buf[offset + 4],
        };
    }
}
