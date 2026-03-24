using System.Text.Json.Serialization;

namespace CanLinConfig.Models;

// --- Profile JSON schema ---

public class ProfileDefinition
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("lin_config")] public ProfileLinConfig? LinConfig { get; set; }
    [JsonPropertyName("schedule_table")] public ProfileScheduleEntry[]? ScheduleTable { get; set; }

    // New: array of CAN-LIN mappings (each links a CAN ID to a LIN frame)
    [JsonPropertyName("can_mappings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProfileCanMapping[]? CanMappings { get; set; }

    // Legacy: single control/status (kept for backward compat, not written by new profiles)
    [JsonPropertyName("can_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProfileCanMapping? CanControl { get; set; }
    [JsonPropertyName("can_status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProfileCanMapping? CanStatus { get; set; }

    [JsonPropertyName("parameters")] public ProfileParameter[]? Parameters { get; set; }

    /// <summary>
    /// Get all CAN mappings, converting legacy can_control/can_status if needed.
    /// </summary>
    public List<ProfileCanMapping> GetAllMappings()
    {
        if (CanMappings != null) return [.. CanMappings];

        // Convert legacy format
        var list = new List<ProfileCanMapping>();
        if (CanControl != null)
        {
            CanControl.Direction ??= "control";
            CanControl.Name = string.IsNullOrEmpty(CanControl.Name) ? "Control" : CanControl.Name;
            // Find first publish LIN frame ID
            var pub = ScheduleTable?.FirstOrDefault(e => e.Direction == "publish");
            if (pub != null && CanControl.LinFrameId == 0) CanControl.LinFrameId = pub.Id;
            list.Add(CanControl);
        }
        if (CanStatus != null)
        {
            CanStatus.Direction ??= "status";
            CanStatus.Name = string.IsNullOrEmpty(CanStatus.Name) ? "Status" : CanStatus.Name;
            var sub = ScheduleTable?.FirstOrDefault(e => e.Direction == "subscribe");
            if (sub != null && CanStatus.LinFrameId == 0) CanStatus.LinFrameId = sub.Id;
            list.Add(CanStatus);
        }
        return list;
    }
}

public class ProfileLinConfig
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "master";
    [JsonPropertyName("baudrate")] public uint Baudrate { get; set; } = 19200;
}

public class ProfileScheduleEntry
{
    [JsonPropertyName("id")] public byte Id { get; set; }
    [JsonPropertyName("direction")] public string Direction { get; set; } = "subscribe";
    [JsonPropertyName("dlc")] public byte Dlc { get; set; } = 8;
    [JsonPropertyName("interval_ms")] public ushort IntervalMs { get; set; } = 10;
}

public class ProfileCanMapping
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
    [JsonPropertyName("direction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Direction { get; set; } // "control" (CAN->LIN) or "status" (LIN->CAN)
    [JsonPropertyName("lin_frame_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public byte LinFrameId { get; set; }
    [JsonPropertyName("can_id")] public uint CanId { get; set; }
    [JsonPropertyName("mapping_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MappingMode { get; set; } // "byte" or "signal", null defaults to "byte"
    [JsonPropertyName("mappings")] public ProfileByteMap[]? Mappings { get; set; }
    [JsonPropertyName("signals")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProfileSignal[]? Signals { get; set; }

    public bool IsSignalMode => MappingMode == "signal";

    /// <summary>
    /// Generate byte-level mappings from signal definitions.
    /// For signals that fit within full bytes, creates direct byte mappings.
    /// For partial-byte signals, uses mask to select the relevant bits.
    /// </summary>
    public List<ProfileByteMap> GenerateByteMapsFromSignals()
    {
        if (Signals == null) return [];
        var maps = new List<ProfileByteMap>();

        foreach (var sig in Signals)
        {
            if (sig.ByteOrder == "big_endian")
            {
                // Motorola byte order: complex bit layout, just map affected bytes
                AddMotorolaMaps(maps, sig);
            }
            else
            {
                // Intel (little-endian): sequential bit layout
                AddIntelMaps(maps, sig);
            }
        }

        return maps;
    }

    private static void AddIntelMaps(List<ProfileByteMap> maps, ProfileSignal sig)
    {
        int startBit = sig.StartBit;
        int remaining = sig.BitLength;

        while (remaining > 0)
        {
            int byteIdx = startBit / 8;
            int bitInByte = startBit % 8;
            int bitsThisByte = Math.Min(remaining, 8 - bitInByte);

            byte mask = (byte)(((1 << bitsThisByte) - 1) << bitInByte);

            // Check for duplicate byte mapping (merge masks)
            var existing = maps.FirstOrDefault(m => m.SrcByte == byteIdx && m.DstByte == byteIdx);
            if (existing != null)
                existing.Mask |= mask;
            else
                maps.Add(new ProfileByteMap { SrcByte = (byte)byteIdx, DstByte = (byte)byteIdx, Mask = mask });

            startBit += bitsThisByte;
            remaining -= bitsThisByte;
        }
    }

    private static void AddMotorolaMaps(List<ProfileByteMap> maps, ProfileSignal sig)
    {
        // Motorola: start_bit is MSB position. Bits go right-to-left within byte, then next row.
        int bitPos = sig.StartBit;
        int remaining = sig.BitLength;

        while (remaining > 0)
        {
            int byteIdx = bitPos / 8;
            int bitInByte = bitPos % 8;
            int bitsThisByte = Math.Min(remaining, bitInByte + 1);

            int lowBit = bitInByte - bitsThisByte + 1;
            byte mask = (byte)(((1 << bitsThisByte) - 1) << lowBit);

            var existing = maps.FirstOrDefault(m => m.SrcByte == byteIdx && m.DstByte == byteIdx);
            if (existing != null)
                existing.Mask |= mask;
            else
                maps.Add(new ProfileByteMap { SrcByte = (byte)byteIdx, DstByte = (byte)byteIdx, Mask = mask });

            remaining -= bitsThisByte;
            // Next byte in Motorola: go to bit 7 of next row
            bitPos = (byteIdx + 1) * 8 + 7;
        }
    }
}

public class ProfileByteMap
{
    [JsonPropertyName("src_byte")] public byte SrcByte { get; set; }
    [JsonPropertyName("dst_byte")] public byte DstByte { get; set; }
    [JsonPropertyName("mask")] public byte Mask { get; set; } = 0xFF;
}

public class ProfileSignal
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("start_bit")] public int StartBit { get; set; }
    [JsonPropertyName("bit_length")] public int BitLength { get; set; }
    [JsonPropertyName("byte_order")] public string ByteOrder { get; set; } = "little_endian";
    [JsonPropertyName("is_signed")] public bool IsSigned { get; set; }
    [JsonPropertyName("factor")] public double Factor { get; set; } = 1.0;
    [JsonPropertyName("offset")] public double Offset { get; set; }
    [JsonPropertyName("min")] public double MinValue { get; set; }
    [JsonPropertyName("max")] public double MaxValue { get; set; }
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    [JsonPropertyName("value_descriptions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ValueDescriptions { get; set; }
}

public class ProfileParameter
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "enum"; // "enum" or "numeric"
    [JsonPropertyName("options")] public string[]? Options { get; set; }
    [JsonPropertyName("min")] public int Min { get; set; }
    [JsonPropertyName("max")] public int Max { get; set; } = 255;
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    [JsonPropertyName("can_control_byte")] public byte CanControlByte { get; set; }
    [JsonPropertyName("mask")] public byte Mask { get; set; } = 0xFF;

    // Bit-level fields (optional, for signal-mode profiles)
    [JsonPropertyName("start_bit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StartBit { get; set; }

    [JsonPropertyName("bit_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BitLength { get; set; }

    [JsonPropertyName("byte_order")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ByteOrder { get; set; }

    [JsonPropertyName("is_signed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsSigned { get; set; }

    [JsonPropertyName("factor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Factor { get; set; }

    [JsonPropertyName("offset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Offset { get; set; }

    [JsonPropertyName("frame")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Frame { get; set; } // "control" or "status"

    [JsonPropertyName("value_descriptions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ValueDescriptions { get; set; }

    public bool IsBitLevel => StartBit.HasValue;
}
