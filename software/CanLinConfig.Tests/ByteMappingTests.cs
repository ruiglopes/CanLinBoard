using CanLinConfig.Models;

namespace CanLinConfig.Tests;

public class ByteMappingTests
{
    [Fact]
    public void Serialize_Deserialize_round_trips()
    {
        var bm = new ByteMapping { SrcByte = 3, DstByte = 5, Mask = 0xF0, Shift = -2, Offset = 10 };
        var bytes = bm.Serialize();
        Assert.Equal(ByteMapping.PackedSize, bytes.Length);

        var restored = ByteMapping.Deserialize(bytes, 0);
        Assert.Equal(3, restored.SrcByte);
        Assert.Equal(5, restored.DstByte);
        Assert.Equal(0xF0, restored.Mask);
        Assert.Equal(-2, restored.Shift);
        Assert.Equal(10, restored.Offset);
    }

    [Fact]
    public void SrcByte_clamps_to_7()
    {
        var bm = new ByteMapping();
        bm.SrcByte = 10;
        Assert.Equal(7, bm.SrcByte);
    }

    [Fact]
    public void DstByte_clamps_to_7()
    {
        var bm = new ByteMapping();
        bm.DstByte = 255;
        Assert.Equal(7, bm.DstByte);
    }
}
