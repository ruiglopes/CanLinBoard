using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace CanLinConfig.Parsers;

// --- DBC Data Model ---

public class DbcFile
{
    public List<DbcMessage> Messages { get; } = [];
    public Dictionary<(uint MsgId, string SigName), Dictionary<int, string>> ValueDescriptions { get; } = [];
    public Dictionary<(uint MsgId, string SigName), string> Comments { get; } = [];
}

public class DbcMessage
{
    public uint Id { get; set; }
    public string Name { get; set; } = "";
    public byte Dlc { get; set; }
    public string Sender { get; set; } = "";
    public List<DbcSignal> Signals { get; } = [];
    public string? Comment { get; set; }

    public override string ToString() => $"{Name} (0x{Id:X3}, {Dlc}B, {Signals.Count} signals)";
}

public class DbcSignal
{
    public string Name { get; set; } = "";
    public int StartBit { get; set; }
    public int BitLength { get; set; }
    public bool IsLittleEndian { get; set; } // true=Intel(@1), false=Motorola(@0)
    public bool IsSigned { get; set; }
    public double Factor { get; set; } = 1.0;
    public double Offset { get; set; }
    public double MinValue { get; set; }
    public double MaxValue { get; set; }
    public string Unit { get; set; } = "";
    public List<string> Receivers { get; } = [];
    public string? Comment { get; set; }
    public Dictionary<int, string> ValueDescriptions { get; } = [];

    // Computed: byte range for display
    public int StartByte => IsLittleEndian ? StartBit / 8 : MotorolaStartByte();
    public int ByteSpan => (BitLength + (StartBit % 8) + 7) / 8;

    private int MotorolaStartByte()
    {
        // Motorola bit numbering: start_bit is MSB position
        // Row = bit / 8, but bits count down within each row
        return StartBit / 8;
    }

    public override string ToString()
    {
        string sign = IsSigned ? "-" : "+";
        string endian = IsLittleEndian ? "LE" : "BE";
        return $"{Name} [{StartBit}|{BitLength}@{endian}{sign}] ({Factor},{Offset}) [{MinValue}|{MaxValue}] \"{Unit}\"";
    }
}

// --- DBC Parser ---

public static class DbcParser
{
    // BO_ <id> <name>: <dlc> <sender>
    private static readonly Regex MsgRegex = new(
        @"^BO_\s+(\d+)\s+(\w+)\s*:\s*(\d+)\s+(\w+)",
        RegexOptions.Compiled);

    // SG_ <name> : <startbit>|<length>@<byteorder><sign> (<factor>,<offset>) [<min>|<max>] "<unit>" <receivers>
    private static readonly Regex SigRegex = new(
        @"^\s+SG_\s+(\w+)\s*:\s*(\d+)\|(\d+)@([01])([+-])\s*\(([^,]+),([^)]+)\)\s*\[([^|]+)\|([^\]]+)\]\s*""([^""]*)""\s*(.*)",
        RegexOptions.Compiled);

    // VAL_ <msg_id> <sig_name> <value> "<desc>" ... ;
    private static readonly Regex ValRegex = new(
        @"^VAL_\s+(\d+)\s+(\w+)\s+(.*);",
        RegexOptions.Compiled);

    // CM_ BO_ <msg_id> "<comment>";
    private static readonly Regex CmMsgRegex = new(
        @"^CM_\s+BO_\s+(\d+)\s+""((?:[^""\\]|\\.)*)""\s*;",
        RegexOptions.Compiled);

    // CM_ SG_ <msg_id> <sig_name> "<comment>";
    private static readonly Regex CmSigRegex = new(
        @"^CM_\s+SG_\s+(\d+)\s+(\w+)\s+""((?:[^""\\]|\\.)*)""\s*;",
        RegexOptions.Compiled);

    public static DbcFile Parse(string filePath)
    {
        var dbc = new DbcFile();
        var lines = File.ReadAllLines(filePath);
        DbcMessage? currentMsg = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            // Message definition
            var msgMatch = MsgRegex.Match(line);
            if (msgMatch.Success)
            {
                currentMsg = new DbcMessage
                {
                    Id = uint.Parse(msgMatch.Groups[1].Value) & 0x1FFFFFFF, // strip extended flag
                    Name = msgMatch.Groups[2].Value,
                    Dlc = byte.Parse(msgMatch.Groups[3].Value),
                    Sender = msgMatch.Groups[4].Value,
                };
                dbc.Messages.Add(currentMsg);
                continue;
            }

            // Signal definition (must follow a message)
            var sigMatch = SigRegex.Match(line);
            if (sigMatch.Success && currentMsg != null)
            {
                var sig = new DbcSignal
                {
                    Name = sigMatch.Groups[1].Value,
                    StartBit = int.Parse(sigMatch.Groups[2].Value),
                    BitLength = int.Parse(sigMatch.Groups[3].Value),
                    IsLittleEndian = sigMatch.Groups[4].Value == "1",
                    IsSigned = sigMatch.Groups[5].Value == "-",
                    Factor = ParseDouble(sigMatch.Groups[6].Value),
                    Offset = ParseDouble(sigMatch.Groups[7].Value),
                    MinValue = ParseDouble(sigMatch.Groups[8].Value),
                    MaxValue = ParseDouble(sigMatch.Groups[9].Value),
                    Unit = sigMatch.Groups[10].Value,
                };

                var receivers = sigMatch.Groups[11].Value.Trim();
                if (!string.IsNullOrEmpty(receivers))
                {
                    foreach (var r in receivers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        sig.Receivers.Add(r);
                }

                currentMsg.Signals.Add(sig);
                continue;
            }

            // Empty line resets current message context
            if (string.IsNullOrWhiteSpace(line))
            {
                currentMsg = null;
                continue;
            }

            // Value descriptions
            var valMatch = ValRegex.Match(line);
            if (valMatch.Success)
            {
                uint msgId = uint.Parse(valMatch.Groups[1].Value) & 0x1FFFFFFF;
                string sigName = valMatch.Groups[2].Value;
                var valDescs = ParseValueDescriptions(valMatch.Groups[3].Value);

                var key = (msgId, sigName);
                dbc.ValueDescriptions[key] = valDescs;

                // Also attach to the signal directly
                var msg = dbc.Messages.FirstOrDefault(m => m.Id == msgId);
                var signal = msg?.Signals.FirstOrDefault(s => s.Name == sigName);
                if (signal != null)
                {
                    foreach (var kv in valDescs)
                        signal.ValueDescriptions[kv.Key] = kv.Value;
                }
                continue;
            }

            // Message comments
            var cmMsgMatch = CmMsgRegex.Match(line);
            if (cmMsgMatch.Success)
            {
                uint msgId = uint.Parse(cmMsgMatch.Groups[1].Value) & 0x1FFFFFFF;
                var msg = dbc.Messages.FirstOrDefault(m => m.Id == msgId);
                if (msg != null) msg.Comment = cmMsgMatch.Groups[2].Value;
                continue;
            }

            // Signal comments
            var cmSigMatch = CmSigRegex.Match(line);
            if (cmSigMatch.Success)
            {
                uint msgId = uint.Parse(cmSigMatch.Groups[1].Value) & 0x1FFFFFFF;
                string sigName = cmSigMatch.Groups[2].Value;
                var msg = dbc.Messages.FirstOrDefault(m => m.Id == msgId);
                var signal = msg?.Signals.FirstOrDefault(s => s.Name == sigName);
                if (signal != null) signal.Comment = cmSigMatch.Groups[3].Value;
                continue;
            }
        }

        return dbc;
    }

    private static Dictionary<int, string> ParseValueDescriptions(string text)
    {
        var result = new Dictionary<int, string>();
        // Format: <value> "<description>" <value> "<description>" ...
        var matches = Regex.Matches(text, @"(\d+)\s+""([^""]*)""\s*");
        foreach (Match m in matches)
        {
            if (int.TryParse(m.Groups[1].Value, out int val))
                result[val] = m.Groups[2].Value;
        }
        return result;
    }

    private static double ParseDouble(string s)
    {
        s = s.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return v;
        return 0;
    }
}
