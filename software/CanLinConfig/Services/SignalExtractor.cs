using CanLinConfig.Parsers;

namespace CanLinConfig.Services;

public static class SignalExtractor
{
    public static double ExtractRaw(byte[] data, DbcSignal signal)
    {
        ulong rawBits;
        if (signal.IsLittleEndian)
            rawBits = ExtractIntel(data, signal.StartBit, signal.BitLength);
        else
            rawBits = ExtractMotorola(data, signal.StartBit, signal.BitLength);

        if (signal.IsSigned)
            return SignExtend(rawBits, signal.BitLength);
        return rawBits;
    }

    public static double ExtractPhysical(byte[] data, DbcSignal signal)
    {
        double raw = ExtractRaw(data, signal);
        return raw * signal.Factor + signal.Offset;
    }

    private static ulong ExtractIntel(byte[] data, int startBit, int bitLength)
    {
        ulong result = 0;
        for (int i = 0; i < bitLength; i++)
        {
            int bitPos = startBit + i;
            int byteIdx = bitPos / 8;
            int bitIdx = bitPos % 8;
            if (byteIdx < data.Length && (data[byteIdx] & (1 << bitIdx)) != 0)
                result |= 1UL << i;
        }
        return result;
    }

    private static ulong ExtractMotorola(byte[] data, int startBit, int bitLength)
    {
        ulong result = 0;
        int bitPos = startBit;
        for (int i = bitLength - 1; i >= 0; i--)
        {
            int byteIdx = bitPos / 8;
            int bitIdx = bitPos % 8;
            if (byteIdx < data.Length && (data[byteIdx] & (1 << bitIdx)) != 0)
                result |= 1UL << i;
            if (bitPos % 8 == 0)
                bitPos += 15;
            else
                bitPos--;
        }
        return result;
    }

    private static double SignExtend(ulong value, int bitLength)
    {
        if ((value & (1UL << (bitLength - 1))) != 0)
        {
            ulong mask = ulong.MaxValue << bitLength;
            return (double)(long)(value | mask);
        }
        return value;
    }
}
