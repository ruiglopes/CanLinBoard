namespace CanLinConfig.Helpers;

/// <summary>
/// CRC32 with standard Ethernet polynomial 0x04C11DB7 (reflected: 0xEDB88320).
/// Matches firmware crc32_compute() in util/crc32.c.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = GenerateTable();

    private static uint[] GenerateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            table[i] = crc;
        }
        return table;
    }

    public static uint Compute(byte[] data, int offset, int length)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = offset; i < offset + length; i++)
        {
            crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFF;
    }

    public static uint Compute(byte[] data) => Compute(data, 0, data.Length);
}
