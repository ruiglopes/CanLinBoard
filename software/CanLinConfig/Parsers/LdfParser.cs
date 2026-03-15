using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace CanLinConfig.Parsers;

// --- LDF Data Model ---

public class LdfFile
{
    public string ProtocolVersion { get; set; } = "";
    public string LanguageVersion { get; set; } = "";
    public double SpeedKbps { get; set; }
    public string MasterNode { get; set; } = "";
    public double MasterTimebaseMs { get; set; }
    public double MasterJitterMs { get; set; }
    public List<string> SlaveNodes { get; } = [];
    public List<LdfSignal> Signals { get; } = [];
    public List<LdfFrame> Frames { get; } = [];
    public List<LdfScheduleTable> ScheduleTables { get; } = [];
    public List<LdfSignalEncoding> SignalEncodings { get; } = [];
    public Dictionary<string, string> SignalRepresentations { get; } = []; // signal_name → encoding_name

    public uint BaudRate => (uint)(SpeedKbps * 1000);
}

public class LdfSignal
{
    public string Name { get; set; } = "";
    public int BitSize { get; set; }
    public int InitValue { get; set; }
    public string Publisher { get; set; } = "";
    public List<string> Subscribers { get; } = [];
}

public class LdfFrame
{
    public string Name { get; set; } = "";
    public byte Id { get; set; }
    public string Publisher { get; set; } = "";
    public byte Size { get; set; } // bytes
    public List<LdfFrameSignal> Signals { get; } = [];

    // Derived: is this published by the master (→ "publish") or slave (→ "subscribe")?
    public string DirectionForMaster(string masterNode)
        => Publisher.Equals(masterNode, StringComparison.OrdinalIgnoreCase) ? "publish" : "subscribe";

    public override string ToString() => $"{Name} (ID {Id}, {Size}B, {Publisher})";
}

public class LdfFrameSignal
{
    public string Name { get; set; } = "";
    public int BitOffset { get; set; }
}

public class LdfScheduleTable
{
    public string Name { get; set; } = "";
    public List<LdfScheduleEntry> Entries { get; } = [];

    public double TotalCycleMs => Entries.Sum(e => e.DelayMs);

    public override string ToString() => $"{Name} ({Entries.Count} entries, {TotalCycleMs:F0}ms cycle)";
}

public class LdfScheduleEntry
{
    public string FrameName { get; set; } = "";
    public double DelayMs { get; set; }
}

public class LdfSignalEncoding
{
    public string Name { get; set; } = "";
    public List<LdfEncodingValue> Values { get; } = [];
}

public class LdfEncodingValue
{
    public bool IsPhysical { get; set; } // true=physical_value, false=logical_value
    public int RawValue { get; set; }
    public int RawMax { get; set; }
    public double Factor { get; set; } = 1.0;
    public double Offset { get; set; }
    public string Description { get; set; } = "";
}

// --- LDF Parser ---

public static class LdfParser
{
    public static LdfFile Parse(string filePath)
    {
        var ldf = new LdfFile();
        var text = File.ReadAllText(filePath, System.Text.Encoding.UTF8);

        // Strip comments before parsing — handles /* ... */ block and // line comments
        text = StripComments(text);

        ParseHeader(text, ldf);
        ParseNodes(text, ldf);
        ParseSignals(text, ldf);
        ParseFrames(text, ldf);
        ParseScheduleTables(text, ldf);
        ParseSignalEncodings(text, ldf);
        ParseSignalRepresentations(text, ldf);

        // Link encodings to signals in frames
        LinkEncodings(ldf);

        return ldf;
    }

    private static string StripComments(string text)
    {
        // Remove /* ... */ block comments (lazy match)
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        // Remove // ... line comments
        text = Regex.Replace(text, @"//[^\r\n]*", "");
        return text;
    }

    private static void ParseHeader(string text, LdfFile ldf)
    {
        var m = Regex.Match(text, @"LIN_protocol_version\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
        if (m.Success) ldf.ProtocolVersion = m.Groups[1].Value;

        m = Regex.Match(text, @"LIN_language_version\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
        if (m.Success) ldf.LanguageVersion = m.Groups[1].Value;

        m = Regex.Match(text, @"LIN_speed\s*=\s*([\d.]+)\s*kbps", RegexOptions.IgnoreCase);
        if (m.Success) ldf.SpeedKbps = ParseDouble(m.Groups[1].Value);
    }

    private static void ParseNodes(string text, LdfFile ldf)
    {
        var block = ExtractBlock(text, "Nodes");
        if (block == null) return;

        // Master: <name>, <timebase> ms, <jitter> ms;
        var m = Regex.Match(block, @"Master\s*:\s*(\w+)\s*,\s*([\d.]+)\s*ms\s*,\s*([\d.]+)\s*ms", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            ldf.MasterNode = m.Groups[1].Value;
            ldf.MasterTimebaseMs = ParseDouble(m.Groups[2].Value);
            ldf.MasterJitterMs = ParseDouble(m.Groups[3].Value);
        }

        // Slaves: <name1>, <name2>, ...;
        var sm = Regex.Match(block, @"Slaves\s*:\s*([^;]+)", RegexOptions.IgnoreCase);
        if (sm.Success)
        {
            foreach (var name in sm.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                ldf.SlaveNodes.Add(name);
        }
    }

    private static void ParseSignals(string text, LdfFile ldf)
    {
        // Match standalone "Signals" at line start to avoid Diagnostic_signals
        var block = ExtractBlock(text, @"(?:^|\n)\s*Signals");
        if (block == null) return;

        // <name>: <size>, <init_value>, <publisher>, <subscriber1>, ...;
        // init_value can be decimal, hex (0xFF), or array ({0, 0, 0})
        var matches = Regex.Matches(block,
            @"(\w+)\s*:\s*(\d+)\s*,\s*(\{[^}]*\}|0x[\da-fA-F]+|\d+)\s*,\s*(\w+)\s*(?:,\s*(\w+(?:\s*,\s*\w+)*))?\s*;");
        foreach (Match m in matches)
        {
            var sig = new LdfSignal
            {
                Name = m.Groups[1].Value,
                BitSize = int.Parse(m.Groups[2].Value),
                InitValue = ParseInt(m.Groups[3].Value),
                Publisher = m.Groups[4].Value,
            };
            if (m.Groups[5].Success)
            {
                foreach (var s in m.Groups[5].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    sig.Subscribers.Add(s);
            }
            ldf.Signals.Add(sig);
        }
    }

    private static void ParseFrames(string text, LdfFile ldf)
    {
        // Match standalone "Frames" at line start to avoid Diagnostic_frames / configurable_frames
        var block = ExtractBlock(text, @"(?:^|\n)\s*Frames");
        if (block == null) return;

        // Frame: <name> : <id>, <publisher>, <size> { <signal>, <offset>; ... }
        // ID can be decimal (38) or hex (0x36)
        var frameRegex = new Regex(
            @"(\w+)\s*:\s*(0x[\da-fA-F]+|\d+)\s*,\s*(\w+)\s*,\s*(\d+)\s*\{([^}]*)\}",
            RegexOptions.Singleline);

        foreach (Match fm in frameRegex.Matches(block))
        {
            var frame = new LdfFrame
            {
                Name = fm.Groups[1].Value,
                Id = (byte)ParseInt(fm.Groups[2].Value),
                Publisher = fm.Groups[3].Value,
                Size = byte.Parse(fm.Groups[4].Value),
            };

            var sigBlock = fm.Groups[5].Value;
            var sigMatches = Regex.Matches(sigBlock, @"(\w+)\s*,\s*(\d+)\s*;");
            foreach (Match sm in sigMatches)
            {
                frame.Signals.Add(new LdfFrameSignal
                {
                    Name = sm.Groups[1].Value,
                    BitOffset = int.Parse(sm.Groups[2].Value),
                });
            }

            ldf.Frames.Add(frame);
        }
    }

    private static void ParseScheduleTables(string text, LdfFile ldf)
    {
        var block = ExtractBlock(text, "Schedule_tables");
        if (block == null) return;

        // Each table: <name> { <frame> delay <ms> ms; ... }
        var tableRegex = new Regex(
            @"(\w+)\s*\{([^}]*)\}",
            RegexOptions.Singleline);

        foreach (Match tm in tableRegex.Matches(block))
        {
            var table = new LdfScheduleTable { Name = tm.Groups[1].Value };

            var entryMatches = Regex.Matches(tm.Groups[2].Value,
                @"(\w+)\s+delay\s+([\d.]+)\s*ms\s*;");
            foreach (Match em in entryMatches)
            {
                table.Entries.Add(new LdfScheduleEntry
                {
                    FrameName = em.Groups[1].Value,
                    DelayMs = ParseDouble(em.Groups[2].Value),
                });
            }

            if (table.Entries.Count > 0)
                ldf.ScheduleTables.Add(table);
        }
    }

    private static void ParseSignalEncodings(string text, LdfFile ldf)
    {
        var block = ExtractBlock(text, "Signal_encoding_types");
        if (block == null) return;

        var encRegex = new Regex(
            @"(\w+)\s*\{([^}]*)\}",
            RegexOptions.Singleline);

        foreach (Match em in encRegex.Matches(block))
        {
            var enc = new LdfSignalEncoding { Name = em.Groups[1].Value };
            var body = em.Groups[2].Value;

            // physical_value, <min>, <max>, <factor>, <offset>, "<unit>";
            var physMatches = Regex.Matches(body,
                @"physical_value\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([\d.]+)\s*,\s*(-?[\d.]+)\s*,\s*""([^""]*)""\s*;");
            foreach (Match pm in physMatches)
            {
                enc.Values.Add(new LdfEncodingValue
                {
                    IsPhysical = true,
                    RawValue = int.Parse(pm.Groups[1].Value),
                    RawMax = int.Parse(pm.Groups[2].Value),
                    Factor = ParseDouble(pm.Groups[3].Value),
                    Offset = ParseDouble(pm.Groups[4].Value),
                    Description = pm.Groups[5].Value,
                });
            }

            // logical_value, <value>, "<description>";
            var logMatches = Regex.Matches(body,
                @"logical_value\s*,\s*(\d+)\s*,\s*""([^""]*)""\s*;");
            foreach (Match lm in logMatches)
            {
                enc.Values.Add(new LdfEncodingValue
                {
                    IsPhysical = false,
                    RawValue = int.Parse(lm.Groups[1].Value),
                    Description = lm.Groups[2].Value,
                });
            }

            ldf.SignalEncodings.Add(enc);
        }
    }

    private static void ParseSignalRepresentations(string text, LdfFile ldf)
    {
        var block = ExtractBlock(text, "Signal_representation");
        if (block == null) return;

        var matches = Regex.Matches(block,
            @"(\w+)\s*:\s*([^;]+);");
        foreach (Match m in matches)
        {
            string encodingName = m.Groups[1].Value;
            foreach (var sigName in m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                ldf.SignalRepresentations[sigName] = encodingName;
            }
        }
    }

    private static void LinkEncodings(LdfFile ldf)
    {
        // This method could enrich signals with encoding data if needed in the future
    }

    /// <summary>
    /// Get the signal encoding for a given signal name.
    /// </summary>
    public static LdfSignalEncoding? GetEncodingForSignal(LdfFile ldf, string signalName)
    {
        if (ldf.SignalRepresentations.TryGetValue(signalName, out var encName))
            return ldf.SignalEncodings.FirstOrDefault(e => e.Name == encName);
        return null;
    }

    /// <summary>
    /// Get the LdfSignal definition by name.
    /// </summary>
    public static LdfSignal? GetSignal(LdfFile ldf, string name)
        => ldf.Signals.FirstOrDefault(s => s.Name == name);

    private static string? ExtractBlock(string text, string keyword)
    {
        // Find keyword followed by { and extract to matching }
        var regex = new Regex(keyword + @"\s*\{", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var m = regex.Match(text);
        if (!m.Success) return null;

        int braceStart = m.Index + m.Length - 1; // position of '{'
        int depth = 1;
        int i = braceStart + 1;

        while (i < text.Length && depth > 0)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') depth--;
            i++;
        }

        if (depth != 0) return null;
        return text.Substring(braceStart + 1, i - braceStart - 2);
    }

    /// <summary>
    /// Parse an integer that may be decimal or hex (0x prefix).
    /// Returns 0 for unparseable values (e.g. array init values).
    /// </summary>
    private static int ParseInt(string s)
    {
        s = s.Trim();
        if (s.StartsWith("{")) return 0; // array init value
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex) ? hex : 0;
        return int.TryParse(s, out int dec) ? dec : 0;
    }

    private static double ParseDouble(string s)
    {
        s = s.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return v;
        return 0;
    }
}
