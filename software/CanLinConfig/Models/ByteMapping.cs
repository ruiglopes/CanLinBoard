using CommunityToolkit.Mvvm.ComponentModel;

namespace CanLinConfig.Models;

public partial class ByteMapping : ObservableObject
{
    private byte _srcByte;
    private byte _dstByte;

    public byte SrcByte
    {
        get => _srcByte;
        set => SetProperty(ref _srcByte, value <= 7 ? value : (byte)7);
    }

    public byte DstByte
    {
        get => _dstByte;
        set => SetProperty(ref _dstByte, value <= 7 ? value : (byte)7);
    }
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
