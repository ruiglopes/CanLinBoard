using System;
using System.Collections.Generic;
using System.Text;

namespace CanBus;

public class DebugMessage
{
    public DateTime Timestamp { get; set; }
    public byte Level { get; set; }
    public string Tag { get; set; } = "";
    public uint Value { get; set; }

    public string LevelName => Level switch
    {
        0 => "ERROR",
        1 => "WARN",
        2 => "INFO",
        3 => "VERBOSE",
        _ => $"L{Level}"
    };

    public string ValueHex => $"0x{Value:X8}";
    public string TimestampStr => Timestamp.ToString("HH:mm:ss.fff");

    private static readonly string[] BootStateNames =
        { "CHECK_MAGIC", "CHECK_BUTTON", "CHECK_APP", "CAN_WAIT", "JUMP_APP", "ENTER_BL" };

    private static readonly Dictionary<string, string> TagDescriptions = new()
    {
        { "INI", "Bootloader initialized" },
        { "BLM", "Entering bootloader mode" },
        { "BOT", "Boot state" },
        { "APP", "App validation" },
        { "CON", "Client connected" },
        { "ERA", "Erasing flash" },
        { "DAT", "Data received" },
        { "DND", "Data transfer complete" },
        { "VER", "Verify CRC" },
        { "RST", "Reset" },
        { "DBG", "Debug toggle" },
        { "PAG", "Page write" },
        { "FLE", "Flash write error" },
        { "ERF", "Erase error" },
        { "ERD", "Erase done" },
        { "SEQ", "Sequence mismatch" },
        { "SUS", "Self-update size error" },
        { "SUV", "Self-update validation" },
        { "SUC", "Self-update commit" },
        { "RRS", "Reset reason" },
        { "ABT", "Abort" },
        { "STO", "Session timeout" },
        { "AUT", "Authentication" },
        { "ARB", "Anti-rollback reject" },
        { "FEC", "Flash error count" },
        { "FBT", "Fast boot (fingerprint skip)" },
        { "SIG", "Signature check" },
        { "APV", "App validation error" },
        { "AKP", "Auth key provision rejected" },
        { "CFG", "Config write error" },
    };

    public string Description
    {
        get
        {
            if (!TagDescriptions.TryGetValue(Tag, out var desc))
                return "";

            return Tag switch
            {
                "BOT" => Value < (uint)BootStateNames.Length
                    ? $"{desc}: {BootStateNames[Value]}" : $"{desc}: {Value}",
                "APP" => $"{desc}: {(Value == 0 ? "invalid" : "valid")}",
                "ERA" => $"{desc} ({Value} bytes)",
                "DAT" => $"{desc} ({Value} bytes)",
                "DND" => $"{desc} ({Value} bytes)",
                "VER" => $"{desc}: 0x{Value:X8}",
                "RST" => $"{desc}: {(Value == 0 ? "to app" : "to bootloader")}",
                "DBG" => $"{desc}: {(Value == 0 ? "off" : "on")}",
                "PAG" => $"{desc} @ 0x{Value:X8}",
                "FLE" => $"{desc} @ 0x{Value:X8}",
                "ERF" => $"{desc} @ 0x{Value:X8}",
                "ERD" => $"{desc} ({Value} sectors)",
                "SEQ" => $"{desc}: expected {Value >> 8}, got {Value & 0xFF}",
                "SUC" => $"{desc} ({Value} bytes)",
                "RRS" => FormatResetReason(Value, desc),
                "ABT" => $"{desc} (from state {Value})",
                "AUT" => $"{desc}: {(Value == 0 ? "failed" : "OK")}",
                "ARB" => $"{desc} (ver 0x{Value:X8})",
                "FEC" => $"{desc}: {Value} error(s)",
                "SIG" => $"{desc}: {(Value == 0 ? "missing" : "mismatch")}",
                "APV" => $"{desc}: reset vector 0x{Value:X8}",
                "CFG" => $"{desc}: code {Value}",
                _ => desc,
            };
        }
    }

    private static readonly string[] ResetReasonNames =
        { "POR", "BOR", "RUN", "WDG", "DEBUG", "GLITCH", "SWCORE_PD", "UNKNOWN" };

    private static string FormatResetReason(uint raw, string desc)
    {
        // Low byte = encoded reason, upper 16 bits = CHIP_RESET flags
        byte encoded = (byte)(raw & 0xFF);
        string name = encoded < ResetReasonNames.Length ? ResetReasonNames[encoded] : $"0x{encoded:X2}";

        var flags = new List<string>();
        if ((raw & 0x00010000) != 0) flags.Add("POR");
        if ((raw & 0x00020000) != 0) flags.Add("BOR");
        if ((raw & 0x00040000) != 0) flags.Add("RUN");
        if ((raw & 0x00080000) != 0) flags.Add("DP_RESET");
        if ((raw & 0x00200000) != 0) flags.Add("RESCUE");
        if ((raw & 0x01000000) != 0) flags.Add("WDG_SWCORE");
        if ((raw & 0x10000000) != 0) flags.Add("WDG_RSM");
        if ((raw & 0x04000000) != 0) flags.Add("GLITCH");

        string detail = flags.Count > 0 ? string.Join("+", flags) : "none";
        return $"{desc}: {name} (CHIP_RESET={detail})";
    }

    public static DebugMessage? TryParse(byte[] data)
    {
        if (data.Length < 8) return null;

        return new DebugMessage
        {
            Timestamp = DateTime.Now,
            Level = data[0],
            Tag = Encoding.ASCII.GetString(data, 1, 3),
            Value = BitConverter.ToUInt32(data, 4)
        };
    }

    public override string ToString() =>
        $"[{TimestampStr}] {LevelName,-7} {Tag} {Value} ({ValueHex})";
}
