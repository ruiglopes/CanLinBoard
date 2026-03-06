using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CanLinConfig.Protocol;

namespace CanLinConfig.Models;

public partial class RoutingRule : ObservableObject
{
    [ObservableProperty] private byte _srcBus;
    [ObservableProperty] private uint _srcId;
    [ObservableProperty] private uint _srcMask = 0x7FF;
    [ObservableProperty] private byte _dstBus;
    [ObservableProperty] private uint _dstId = 0xFFFFFFFF; // passthrough
    [ObservableProperty] private byte _dstDlc; // 0=auto
    [ObservableProperty] private bool _enabled = true;
    public ObservableCollection<ByteMapping> Mappings { get; } = [];

    // Software-only fields (not serialized to firmware)
    [JsonIgnore] public string ProfileTag { get; set; } = "";
    [ObservableProperty] private bool _bitMode;
    public ObservableCollection<BitMapping> BitMappings { get; } = [];

    public string SrcBusName => BusName(SrcBus);
    public string DstBusName => BusName(DstBus);
    public string SrcIdHex => $"0x{SrcId:X3}";
    public string DstIdHex => DstId == 0xFFFFFFFF ? "Passthrough" : $"0x{DstId:X3}";

    public static string BusName(byte bus) => bus switch
    {
        0 => "CAN1", 1 => "CAN2",
        2 => "LIN1", 3 => "LIN2", 4 => "LIN3", 5 => "LIN4",
        _ => $"Bus{bus}",
    };

    /// <summary>
    /// Serialize to match firmware routing_rule_t memory layout.
    /// This must match the ARM target struct exactly.
    /// bus_id_t is an enum (4 bytes on ARM GCC).
    /// </summary>
    public byte[] Serialize()
    {
        // routing_rule_t layout (ARM GCC, no packing on inner struct):
        //   bus_id_t src_bus       (4 bytes, enum=int)
        //   uint32_t src_id        (4 bytes)
        //   uint32_t src_mask      (4 bytes)
        //   bus_id_t dst_bus       (4 bytes)
        //   uint32_t dst_id        (4 bytes)
        //   uint8_t  dst_dlc       (1 byte)
        //   uint8_t  mapping_count (1 byte)
        //   byte_mapping_t[8]      (40 bytes)
        //   bool     enabled       (1 byte)
        //   + padding to alignment
        // Total estimated: needs sizeof verification on target

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write((int)SrcBus);     // 4 bytes
        bw.Write(SrcId);           // 4 bytes
        bw.Write(SrcMask);         // 4 bytes
        bw.Write((int)DstBus);     // 4 bytes
        bw.Write(DstId);           // 4 bytes
        bw.Write(DstDlc);          // 1 byte
        bw.Write((byte)Mappings.Count); // 1 byte

        // 8 mappings * 5 bytes = 40 bytes
        for (int i = 0; i < ProtocolConstants.MaxByteMappings; i++)
        {
            if (i < Mappings.Count)
                bw.Write(Mappings[i].Serialize());
            else
                bw.Write(new byte[ByteMapping.PackedSize]);
        }

        bw.Write(Enabled);         // 1 byte (bool)

        // Pad to next 4-byte boundary
        int pos = (int)ms.Position;
        int pad = (4 - (pos % 4)) % 4;
        for (int i = 0; i < pad; i++) bw.Write((byte)0);

        return ms.ToArray();
    }

    public static RoutingRule Deserialize(byte[] buf, int offset, int ruleSize)
    {
        using var ms = new MemoryStream(buf, offset, ruleSize);
        using var br = new BinaryReader(ms);

        var rule = new RoutingRule
        {
            SrcBus = (byte)br.ReadInt32(),
            SrcId = br.ReadUInt32(),
            SrcMask = br.ReadUInt32(),
            DstBus = (byte)br.ReadInt32(),
            DstId = br.ReadUInt32(),
            DstDlc = br.ReadByte(),
        };

        byte mapCount = br.ReadByte();

        for (int i = 0; i < ProtocolConstants.MaxByteMappings; i++)
        {
            var mapBytes = br.ReadBytes(ByteMapping.PackedSize);
            if (i < mapCount)
                rule.Mappings.Add(ByteMapping.Deserialize(mapBytes, 0));
        }

        rule.Enabled = br.ReadBoolean();

        return rule;
    }
}
