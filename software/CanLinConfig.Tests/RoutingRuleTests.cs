using CanLinConfig.Models;
using CanLinConfig.Protocol;

namespace CanLinConfig.Tests;

public class RoutingRuleTests
{
    [Fact]
    public void Serialize_produces_expected_size()
    {
        var rule = new RoutingRule { SrcBus = 0, SrcId = 0x100, DstBus = 1, DstId = 0x200 };
        var bytes = rule.Serialize();
        Assert.Equal(ProtocolConstants.ExpectedRoutingRuleSize, bytes.Length);
    }

    [Fact]
    public void Serialize_Deserialize_round_trips()
    {
        var rule = new RoutingRule
        {
            SrcBus = 0, SrcId = 0x123, SrcMask = 0x7FF,
            DstBus = 1, DstId = 0x456, DstDlc = 4, Enabled = true
        };
        rule.Mappings.Add(new ByteMapping { SrcByte = 0, DstByte = 2, Mask = 0xFF, Shift = 0, Offset = 0 });
        rule.Mappings.Add(new ByteMapping { SrcByte = 1, DstByte = 3, Mask = 0x0F, Shift = 2, Offset = -1 });

        var bytes = rule.Serialize();
        var restored = RoutingRule.Deserialize(bytes, 0, bytes.Length);

        Assert.Equal(rule.SrcBus, restored.SrcBus);
        Assert.Equal(rule.SrcId, restored.SrcId);
        Assert.Equal(rule.SrcMask, restored.SrcMask);
        Assert.Equal(rule.DstBus, restored.DstBus);
        Assert.Equal(rule.DstId, restored.DstId);
        Assert.Equal(rule.DstDlc, restored.DstDlc);
        Assert.Equal(rule.Enabled, restored.Enabled);
        Assert.Equal(2, restored.Mappings.Count);
        Assert.Equal(0x0F, restored.Mappings[1].Mask);
        Assert.Equal(2, restored.Mappings[1].Shift);
    }

    [Fact]
    public void Serialize_passthrough_id()
    {
        var rule = new RoutingRule { DstId = 0xFFFFFFFF };
        var bytes = rule.Serialize();
        var restored = RoutingRule.Deserialize(bytes, 0, bytes.Length);
        Assert.Equal(0xFFFFFFFFu, restored.DstId);
    }

    [Fact]
    public void Serialize_disabled_rule()
    {
        var rule = new RoutingRule { Enabled = false };
        var bytes = rule.Serialize();
        var restored = RoutingRule.Deserialize(bytes, 0, bytes.Length);
        Assert.False(restored.Enabled);
    }

    [Fact]
    public void DstDlc_clamps_to_8()
    {
        var rule = new RoutingRule();
        rule.DstDlc = 10;
        Assert.Equal(8, rule.DstDlc);
    }
}
