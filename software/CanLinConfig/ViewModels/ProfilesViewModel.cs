using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Adapters;
using CanLinConfig.Models;
using CanLinConfig.Protocol;
using Microsoft.Win32;

namespace CanLinConfig.ViewModels;

// --- Profile JSON schema ---

public class ProfileDefinition
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("lin_config")] public ProfileLinConfig? LinConfig { get; set; }
    [JsonPropertyName("schedule_table")] public ProfileScheduleEntry[]? ScheduleTable { get; set; }
    [JsonPropertyName("can_control")] public ProfileCanMapping? CanControl { get; set; }
    [JsonPropertyName("can_status")] public ProfileCanMapping? CanStatus { get; set; }
    [JsonPropertyName("parameters")] public ProfileParameter[]? Parameters { get; set; }
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
    [JsonPropertyName("can_id")] public uint CanId { get; set; }
    [JsonPropertyName("mappings")] public ProfileByteMap[]? Mappings { get; set; }
}

public class ProfileByteMap
{
    [JsonPropertyName("src_byte")] public byte SrcByte { get; set; }
    [JsonPropertyName("dst_byte")] public byte DstByte { get; set; }
    [JsonPropertyName("mask")] public byte Mask { get; set; } = 0xFF;
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
}

// --- Runtime parameter state ---

public partial class ProfileParameterValue : ObservableObject
{
    public ProfileParameter Definition { get; }

    [ObservableProperty] private int _value;
    [ObservableProperty] private int _selectedOptionIndex;

    public bool IsEnum => Definition.Type == "enum";
    public bool IsNumeric => Definition.Type == "numeric";

    public ProfileParameterValue(ProfileParameter def)
    {
        Definition = def;
    }

    public byte GetByteValue()
    {
        if (IsEnum && Definition.Options != null)
            return (byte)(SelectedOptionIndex & (Definition.Mask & 0xFF));
        return (byte)(Value & (Definition.Mask & 0xFF));
    }
}

// --- ViewModel ---

public partial class ProfilesViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public ObservableCollection<ProfileDefinition> Profiles { get; } = [];
    [ObservableProperty] private ProfileDefinition? _selectedProfile;
    [ObservableProperty] private int _assignedChannel; // 0-3
    [ObservableProperty] private bool _profileApplied;

    // Profile enable params (firmware)
    [ObservableProperty] private bool _wdaEnabled;
    [ObservableProperty] private byte _wdaChannel;
    [ObservableProperty] private bool _cwa400Enabled;
    [ObservableProperty] private byte _cwa400Channel;

    // Active parameter controls (populated after ApplyProfile)
    public ObservableCollection<ProfileParameterValue> ActiveParameterValues { get; } = [];

    public ProfilesViewModel(MainViewModel main)
    {
        _main = main;
        LoadProfiles();
    }

    partial void OnSelectedProfileChanged(ProfileDefinition? value)
    {
        ActiveParameterValues.Clear();
        ProfileApplied = false;
    }

    private void LoadProfiles()
    {
        Profiles.Clear();
        var devicesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles", "Devices");
        if (!Directory.Exists(devicesDir)) return;

        foreach (var file in Directory.GetFiles(devicesDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var profile = JsonSerializer.Deserialize<ProfileDefinition>(json);
                if (profile != null) Profiles.Add(profile);
            }
            catch { }
        }
    }

    // --- Step 1: Routing rule generation ---

    private static List<RoutingRule> GenerateRoutingRules(ProfileDefinition profile, int linChannel)
    {
        var rules = new List<RoutingRule>();
        byte linBus = (byte)(2 + linChannel); // LIN1=2, LIN2=3, LIN3=4, LIN4=5
        string tag = $"{profile.Id}:{linChannel}";

        // Find the publish entry ID (for can_control -> LIN) and subscribe entry ID (for LIN -> can_status)
        byte? publishId = null;
        byte? subscribeId = null;
        if (profile.ScheduleTable != null)
        {
            foreach (var entry in profile.ScheduleTable)
            {
                if (entry.Direction == "publish" && publishId == null) publishId = entry.Id;
                if (entry.Direction == "subscribe" && subscribeId == null) subscribeId = entry.Id;
            }
        }

        // can_control: CAN1 -> LIN (publish frame)
        if (profile.CanControl != null && publishId != null)
        {
            var rule = new RoutingRule
            {
                Enabled = true,
                SrcBus = 0, // CAN1
                SrcId = profile.CanControl.CanId,
                SrcMask = 0x7FF,
                DstBus = linBus,
                DstId = publishId.Value,
                DstDlc = 0, // auto
                ProfileTag = tag,
            };
            if (profile.CanControl.Mappings != null)
            {
                foreach (var m in profile.CanControl.Mappings)
                    rule.Mappings.Add(new ByteMapping { SrcByte = m.SrcByte, DstByte = m.DstByte, Mask = m.Mask });
            }
            rules.Add(rule);
        }

        // can_status: LIN -> CAN1 (subscribe frame)
        if (profile.CanStatus != null && subscribeId != null)
        {
            var rule = new RoutingRule
            {
                Enabled = true,
                SrcBus = linBus,
                SrcId = subscribeId.Value,
                SrcMask = 0x3F, // LIN ID mask (6-bit)
                DstBus = 0, // CAN1
                DstId = profile.CanStatus.CanId,
                DstDlc = 0, // auto
                ProfileTag = tag,
            };
            if (profile.CanStatus.Mappings != null)
            {
                foreach (var m in profile.CanStatus.Mappings)
                    rule.Mappings.Add(new ByteMapping { SrcByte = m.SrcByte, DstByte = m.DstByte, Mask = m.Mask });
            }
            rules.Add(rule);
        }

        return rules;
    }

    [RelayCommand]
    private async Task ApplyProfile()
    {
        if (SelectedProfile == null || _main.Protocol == null) return;

        _main.StatusBarText = $"Applying profile: {SelectedProfile.Name}...";

        var proto = _main.Protocol;
        byte ch = (byte)AssignedChannel;
        var linVm = _main.LinConfig.Channels[ch];

        // Update UI + write LIN channel config to device
        if (SelectedProfile.LinConfig != null)
        {
            byte mode = SelectedProfile.LinConfig.Mode == "master" ? (byte)1 : (byte)2;
            linVm.Enabled = true;
            linVm.Mode = mode;
            linVm.Baudrate = SelectedProfile.LinConfig.Baudrate;

            await proto.WriteParamAsync(ProtocolConstants.SectionLin, 0, ch, [1]); // enabled
            await proto.WriteParamAsync(ProtocolConstants.SectionLin, 1, ch, [mode]);
            uint br = SelectedProfile.LinConfig.Baudrate;
            await proto.WriteParamAsync(ProtocolConstants.SectionLin, 2, ch,
                [(byte)br, (byte)(br >> 8), (byte)(br >> 16)]);
        }

        // Update UI + write schedule table to device
        linVm.Schedule.Clear();
        if (SelectedProfile.ScheduleTable != null)
        {
            foreach (var entry in SelectedProfile.ScheduleTable)
            {
                linVm.Schedule.Add(new LinScheduleEntry
                {
                    Id = entry.Id,
                    Dlc = entry.Dlc,
                    Direction = (byte)(entry.Direction == "publish" ? 1 : 0),
                    DelayMs = entry.IntervalMs,
                });
            }
        }
        var scheduleData = linVm.SerializeSchedule();
        await proto.BulkWriteAsync(ProtocolConstants.SectionLin, ch, scheduleData);

        // Generate and apply routing rules
        string tag = $"{SelectedProfile.Id}:{AssignedChannel}";
        var newRules = GenerateRoutingRules(SelectedProfile, AssignedChannel);

        // Remove existing rules with same tag
        for (int i = _main.Routing.Rules.Count - 1; i >= 0; i--)
        {
            if (_main.Routing.Rules[i].ProfileTag == tag)
                _main.Routing.Rules.RemoveAt(i);
        }

        // Check capacity
        if (_main.Routing.Rules.Count + newRules.Count > ProtocolConstants.MaxRoutingRules)
        {
            MessageBox.Show(
                $"Adding {newRules.Count} rules would exceed the maximum of {ProtocolConstants.MaxRoutingRules}.\n" +
                "Remove some existing rules first.",
                "Rule Limit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (newRules.Count > 0)
        {
            foreach (var rule in newRules)
                _main.Routing.Rules.Add(rule);

            await _main.Routing.WriteToDeviceAsync(proto);
        }

        // Populate parameter controls
        ActiveParameterValues.Clear();
        if (SelectedProfile.Parameters != null)
        {
            foreach (var p in SelectedProfile.Parameters)
                ActiveParameterValues.Add(new ProfileParameterValue(p));
        }
        ProfileApplied = true;

        // Switch to LIN tab showing the affected channel
        _main.LinConfig.SelectedChannelIndex = ch;

        _main.StatusBarText = $"Profile '{SelectedProfile.Name}' applied to LIN{ch + 1} with {newRules.Count} routing rules. Click Save NVM to persist.";
    }

    // --- Step 2: Import/Export ---

    [RelayCommand]
    private void ImportProfile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "JSON Profile|*.json",
            DefaultExt = ".json",
            Title = "Import Device Profile",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var profile = JsonSerializer.Deserialize<ProfileDefinition>(json);
            if (profile == null || string.IsNullOrEmpty(profile.Name) || string.IsNullOrEmpty(profile.Id))
            {
                MessageBox.Show("Invalid profile: missing 'name' or 'id' field.", "Import Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Check for duplicate
            var existing = Profiles.FirstOrDefault(p => p.Id == profile.Id);
            if (existing != null)
            {
                var result = MessageBox.Show(
                    $"Profile '{existing.Name}' (id: {existing.Id}) already exists. Replace it?",
                    "Duplicate Profile", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;
                Profiles.Remove(existing);
            }

            // Copy file to Profiles/Devices/
            var devicesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles", "Devices");
            Directory.CreateDirectory(devicesDir);
            var destPath = Path.Combine(devicesDir, $"{profile.Id}.json");
            File.Copy(dlg.FileName, destPath, overwrite: true);

            Profiles.Add(profile);
            SelectedProfile = profile;
            _main.StatusBarText = $"Imported profile: {profile.Name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed: {ex.Message}", "Import Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ExportProfile()
    {
        if (SelectedProfile == null) return;

        var dlg = new SaveFileDialog
        {
            Filter = "JSON Profile|*.json",
            DefaultExt = ".json",
            FileName = $"{SelectedProfile.Id}.json",
            Title = "Export Device Profile",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = JsonSerializer.Serialize(SelectedProfile, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            _main.StatusBarText = $"Exported profile: {SelectedProfile.Name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "Export Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- Step 3: Send control frame ---

    [RelayCommand]
    private void SendControlFrame()
    {
        if (SelectedProfile?.CanControl == null || !ProfileApplied || ActiveParameterValues.Count == 0) return;

        var data = new byte[8];
        foreach (var pv in ActiveParameterValues)
        {
            byte idx = pv.Definition.CanControlByte;
            if (idx < 8)
            {
                byte mask = pv.Definition.Mask;
                data[idx] = (byte)((data[idx] & ~mask) | (pv.GetByteValue() & mask));
            }
        }

        var frame = new CanFrame(SelectedProfile.CanControl.CanId, data, (byte)data.Length);
        _main.SendRawFrame(frame);
        _main.StatusBarText = $"Sent control frame 0x{frame.Id:X3}: {frame}";
    }

    // --- Device read/write ---

    public async Task ReadFromDeviceAsync(ConfigProtocol proto)
    {
        var wda = await proto.ReadParamAsync(ProtocolConstants.SectionProfiles, 0, 0);
        if (wda.Success && wda.Value.Length >= 1) WdaEnabled = wda.Value[0] != 0;

        var cwa = await proto.ReadParamAsync(ProtocolConstants.SectionProfiles, 1, 0);
        if (cwa.Success && cwa.Value.Length >= 1) Cwa400Enabled = cwa.Value[0] != 0;
    }

    public async Task WriteToDeviceAsync(ConfigProtocol proto)
    {
        await proto.WriteParamAsync(ProtocolConstants.SectionProfiles, 0, 0,
            [(byte)(WdaEnabled ? 1 : 0), WdaChannel]);
        await proto.WriteParamAsync(ProtocolConstants.SectionProfiles, 1, 0,
            [(byte)(Cwa400Enabled ? 1 : 0), Cwa400Channel]);
    }
}
