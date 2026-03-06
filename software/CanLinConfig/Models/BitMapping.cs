using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CanLinConfig.Models;

public partial class BitMapping : ObservableObject
{
    [ObservableProperty] private int _srcStartBit;
    [ObservableProperty] private int _bitLength = 8;
    [ObservableProperty] private int _dstStartBit;

    public List<ByteMapping> ToByteMapppings()
    {
        var maps = new List<ByteMapping>();
        int srcBit = SrcStartBit;
        int dstBit = DstStartBit;
        int remaining = BitLength;

        while (remaining > 0)
        {
            int srcByteIdx = srcBit / 8;
            int srcBitInByte = srcBit % 8;
            int dstByteIdx = dstBit / 8;
            int dstBitInByte = dstBit % 8;

            int bitsThisByte = Math.Min(remaining, Math.Min(8 - srcBitInByte, 8 - dstBitInByte));

            byte mask = (byte)(((1 << bitsThisByte) - 1) << srcBitInByte);
            int shift = dstBitInByte - srcBitInByte;

            maps.Add(new ByteMapping
            {
                SrcByte = (byte)srcByteIdx,
                DstByte = (byte)dstByteIdx,
                Mask = mask,
                Shift = (sbyte)shift,
            });

            srcBit += bitsThisByte;
            dstBit += bitsThisByte;
            remaining -= bitsThisByte;
        }

        return maps;
    }
}
