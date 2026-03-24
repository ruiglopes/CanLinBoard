using CanLinConfig.Helpers;

namespace CanLinConfig.Tests;

public class Crc32Tests
{
    [Fact]
    public void Compute_empty_array_returns_zero_crc()
    {
        // CRC32 of empty data = 0x00000000
        Assert.Equal(0x00000000u, Crc32.Compute([]));
    }

    [Fact]
    public void Compute_known_value_123456789()
    {
        // Standard CRC32 test vector: ASCII "123456789" = 0xCBF43926
        var data = System.Text.Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xCBF43926u, Crc32.Compute(data));
    }

    [Fact]
    public void Compute_single_byte()
    {
        // CRC32 of single byte 0x00
        var result = Crc32.Compute([0x00]);
        Assert.Equal(0xD202EF8Du, result);
    }

    [Fact]
    public void Compute_with_offset_and_length()
    {
        var data = new byte[] { 0xFF, 0x31, 0x32, 0x33, 0xFF }; // "123" in the middle
        var full = System.Text.Encoding.ASCII.GetBytes("123");
        Assert.Equal(Crc32.Compute(full), Crc32.Compute(data, 1, 3));
    }
}
