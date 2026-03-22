using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Services;

public class ConfigFileData
{
    [JsonPropertyName("tool_version")] public string ToolVersion { get; set; } = "1.0.0";
    [JsonPropertyName("fw_version")] public string FwVersion { get; set; } = "";
    [JsonPropertyName("export_time")] public string ExportTime { get; set; } = "";
    [JsonPropertyName("can")] public CanBusFileData[] Can { get; set; } = [new(), new()];
    [JsonPropertyName("lin")] public LinChannelFileData[] Lin { get; set; } = [new(), new(), new(), new()];
    [JsonPropertyName("diag")] public DiagFileData Diag { get; set; } = new();
    [JsonPropertyName("routing")] public RoutingRuleFileData[] Routing { get; set; } = [];
}

public class CanBusFileData
{
    [JsonPropertyName("bitrate")] public uint Bitrate { get; set; } = 500000;
    [JsonPropertyName("termination")] public bool Termination { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
}

public class LinChannelFileData
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("mode")] public byte Mode { get; set; }
    [JsonPropertyName("baudrate")] public uint Baudrate { get; set; } = 19200;
    [JsonPropertyName("schedule")] public LinScheduleEntryFileData[] Schedule { get; set; } = [];
}

public class LinScheduleEntryFileData
{
    [JsonPropertyName("id")] public byte Id { get; set; }
    [JsonPropertyName("dlc")] public byte Dlc { get; set; }
    [JsonPropertyName("direction")] public byte Direction { get; set; }
    [JsonPropertyName("data")] public string Data { get; set; } = "";
    [JsonPropertyName("delay_ms")] public ushort DelayMs { get; set; }
    [JsonPropertyName("classic_cs")] public bool ClassicCs { get; set; }
}

public class DiagFileData
{
    [JsonPropertyName("can_id")] public uint CanId { get; set; } = 0x7F0;
    [JsonPropertyName("interval_ms")] public ushort IntervalMs { get; set; } = 1000;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("bus")] public byte Bus { get; set; }
    [JsonPropertyName("can_watchdog_ms")] public ushort CanWatchdogMs { get; set; }
    [JsonPropertyName("lin_watchdog_ms")] public ushort LinWatchdogMs { get; set; }
}

public class RoutingRuleFileData
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("src_bus")] public byte SrcBus { get; set; }
    [JsonPropertyName("src_id")] public uint SrcId { get; set; }
    [JsonPropertyName("src_mask")] public uint SrcMask { get; set; }
    [JsonPropertyName("dst_bus")] public byte DstBus { get; set; }
    [JsonPropertyName("dst_id")] public uint DstId { get; set; }
    [JsonPropertyName("dst_dlc")] public byte DstDlc { get; set; }
    [JsonPropertyName("mappings")] public ByteMappingFileData[] Mappings { get; set; } = [];
    [JsonPropertyName("profile_tag")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string ProfileTag { get; set; } = "";
}

public class ByteMappingFileData
{
    [JsonPropertyName("src_byte")] public byte SrcByte { get; set; }
    [JsonPropertyName("dst_byte")] public byte DstByte { get; set; }
    [JsonPropertyName("mask")] public byte Mask { get; set; }
    [JsonPropertyName("shift")] public sbyte Shift { get; set; }
    [JsonPropertyName("offset")] public sbyte Offset { get; set; }
}

public static class ConfigFileService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public static ConfigFileData ExportFromViewModel(MainViewModel vm)
    {
        var data = new ConfigFileData
        {
            FwVersion = vm.FirmwareVersion,
            ExportTime = DateTime.UtcNow.ToString("o"),
        };

        // CAN
        data.Can[0] = new() { Bitrate = vm.CanConfig.Can1.Bitrate, Termination = vm.CanConfig.Can1.Termination, Enabled = true };
        data.Can[1] = new() { Bitrate = vm.CanConfig.Can2.Bitrate, Termination = vm.CanConfig.Can2.Termination, Enabled = vm.CanConfig.Can2.Enabled };

        // LIN
        for (int i = 0; i < 4; i++)
        {
            var ch = vm.LinConfig.Channels[i];
            data.Lin[i] = new()
            {
                Enabled = ch.Enabled,
                Mode = ch.Mode,
                Baudrate = ch.Baudrate,
                Schedule = ch.Schedule.Select(e => new LinScheduleEntryFileData
                {
                    Id = e.Id, Dlc = e.Dlc, Direction = e.Direction,
                    Data = e.DataHex, DelayMs = e.DelayMs, ClassicCs = e.ClassicChecksum,
                }).ToArray(),
            };
        }

        // Diag
        data.Diag = new()
        {
            CanId = vm.DiagConfig.CanId,
            IntervalMs = vm.DiagConfig.IntervalMs,
            Enabled = vm.DiagConfig.Enabled,
            Bus = vm.DiagConfig.Bus,
            CanWatchdogMs = vm.DiagConfig.CanWatchdogMs,
            LinWatchdogMs = vm.DiagConfig.LinWatchdogMs,
        };

        // Routing
        data.Routing = vm.Routing.Rules.Select(r => new RoutingRuleFileData
        {
            Enabled = r.Enabled, SrcBus = r.SrcBus, SrcId = r.SrcId, SrcMask = r.SrcMask,
            DstBus = r.DstBus, DstId = r.DstId, DstDlc = r.DstDlc,
            ProfileTag = r.ProfileTag,
            Mappings = r.Mappings.Select(m => new ByteMappingFileData
            {
                SrcByte = m.SrcByte, DstByte = m.DstByte, Mask = m.Mask, Shift = m.Shift, Offset = m.Offset,
            }).ToArray(),
        }).ToArray();

        return data;
    }

    public static void ImportToViewModel(ConfigFileData data, MainViewModel vm)
    {
        // CAN
        vm.CanConfig.Can1.Bitrate = data.Can[0].Bitrate;
        vm.CanConfig.Can1.Termination = data.Can[0].Termination;
        vm.CanConfig.Can2.Bitrate = data.Can[1].Bitrate;
        vm.CanConfig.Can2.Termination = data.Can[1].Termination;
        vm.CanConfig.Can2.Enabled = data.Can[1].Enabled;

        // LIN
        for (int i = 0; i < 4 && i < data.Lin.Length; i++)
        {
            var ch = vm.LinConfig.Channels[i];
            ch.Enabled = data.Lin[i].Enabled;
            ch.Mode = data.Lin[i].Mode;
            ch.Baudrate = data.Lin[i].Baudrate;
            ch.Schedule.Clear();
            foreach (var e in data.Lin[i].Schedule)
            {
                ch.Schedule.Add(new Models.LinScheduleEntry
                {
                    Id = e.Id, Dlc = e.Dlc, Direction = e.Direction,
                    DelayMs = e.DelayMs, ClassicChecksum = e.ClassicCs,
                    DataHex = e.Data,
                });
            }
        }

        // Diag
        vm.DiagConfig.CanId = data.Diag.CanId;
        vm.DiagConfig.IntervalMs = data.Diag.IntervalMs;
        vm.DiagConfig.Enabled = data.Diag.Enabled;
        vm.DiagConfig.Bus = data.Diag.Bus;
        vm.DiagConfig.CanWatchdogMs = data.Diag.CanWatchdogMs;
        vm.DiagConfig.LinWatchdogMs = data.Diag.LinWatchdogMs;

        // Routing
        vm.Routing.Rules.Clear();
        foreach (var r in data.Routing)
        {
            var rule = new Models.RoutingRule
            {
                Enabled = r.Enabled, SrcBus = r.SrcBus, SrcId = r.SrcId, SrcMask = r.SrcMask,
                DstBus = r.DstBus, DstId = r.DstId, DstDlc = r.DstDlc,
                ProfileTag = r.ProfileTag ?? "",
            };
            foreach (var m in r.Mappings)
                rule.Mappings.Add(new Models.ByteMapping
                {
                    SrcByte = m.SrcByte, DstByte = m.DstByte, Mask = m.Mask, Shift = m.Shift, Offset = m.Offset,
                });
            vm.Routing.Rules.Add(rule);
        }
    }

    public static bool SaveToFile(MainViewModel vm)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "JSON Config|*.json",
            DefaultExt = ".json",
            FileName = "CanLinConfig.json",
        };
        if (dlg.ShowDialog() != true) return false;

        try
        {
            var data = ExportFromViewModel(vm);
            var json = JsonSerializer.Serialize(data, JsonOpts);
            File.WriteAllText(dlg.FileName, json);
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to save config file:\n{ex.Message}",
                "Export Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return false;
        }
    }

    public static bool LoadFromFile(MainViewModel vm)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "JSON Config|*.json",
            DefaultExt = ".json",
        };
        if (dlg.ShowDialog() != true) return false;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var data = JsonSerializer.Deserialize<ConfigFileData>(json);
            if (data == null)
            {
                System.Windows.MessageBox.Show("Config file is empty or invalid.",
                    "Import Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return false;
            }

            ImportToViewModel(data, vm);
            return true;
        }
        catch (JsonException ex)
        {
            System.Windows.MessageBox.Show($"Malformed JSON config file:\n{ex.Message}",
                "Import Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return false;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to load config file:\n{ex.Message}",
                "Import Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return false;
        }
    }
}
