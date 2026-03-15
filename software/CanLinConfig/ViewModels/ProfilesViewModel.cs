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

    // New: array of CAN↔LIN mappings (each links a CAN ID to a LIN frame)
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
    public string? Direction { get; set; } // "control" (CAN→LIN) or "status" (LIN→CAN)
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

        foreach (var mapping in profile.GetAllMappings())
        {
            if (mapping.Direction == "control")
            {
                // CAN → LIN: CAN frame is source, LIN frame is destination
                var rule = new RoutingRule
                {
                    Enabled = true,
                    SrcBus = 0, // CAN1
                    SrcId = mapping.CanId,
                    SrcMask = 0x7FF,
                    DstBus = linBus,
                    DstId = mapping.LinFrameId,
                    DstDlc = 0, // auto
                    ProfileTag = tag,
                };
                AddByteMappings(rule, mapping);
                rules.Add(rule);
            }
            else // "status"
            {
                // LIN → CAN: LIN frame is source, CAN frame is destination
                var rule = new RoutingRule
                {
                    Enabled = true,
                    SrcBus = linBus,
                    SrcId = mapping.LinFrameId,
                    SrcMask = 0x3F, // LIN ID mask (6-bit)
                    DstBus = 0, // CAN1
                    DstId = mapping.CanId,
                    DstDlc = 0, // auto
                    ProfileTag = tag,
                };
                AddByteMappings(rule, mapping);
                rules.Add(rule);
            }
        }

        return rules;
    }

    private static void AddByteMappings(RoutingRule rule, ProfileCanMapping mapping)
    {
        List<ProfileByteMap> maps;
        if (mapping.IsSignalMode && mapping.Signals != null)
            maps = mapping.GenerateByteMapsFromSignals();
        else if (mapping.Mappings != null)
            maps = [.. mapping.Mappings];
        else
            return;

        foreach (var m in maps)
            rule.Mappings.Add(new ByteMapping { SrcByte = m.SrcByte, DstByte = m.DstByte, Mask = m.Mask });
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

    // --- New / Edit / Delete ---

    [RelayCommand]
    private void NewProfile()
    {
        var profile = new ProfileDefinition
        {
            Name = "New Profile",
            Id = $"new-{DateTime.Now:yyyyMMddHHmmss}",
            Version = "1.0",
            Description = "",
            LinConfig = new ProfileLinConfig(),
            ScheduleTable = [],
            CanMappings = [],
            Parameters = [],
        };

        var editor = new Views.ProfileEditorWindow(profile, isNew: true);
        editor.Owner = Application.Current.MainWindow;
        if (editor.ShowDialog() == true)
        {
            SaveProfileToFile(editor.Profile);
            Profiles.Add(editor.Profile);
            SelectedProfile = editor.Profile;
            _main.StatusBarText = $"Created profile: {editor.Profile.Name}";
        }
    }

    [RelayCommand]
    private void EditProfile()
    {
        if (SelectedProfile == null) return;

        // Deep clone via JSON round-trip so edits can be cancelled
        var json = JsonSerializer.Serialize(SelectedProfile);
        var clone = JsonSerializer.Deserialize<ProfileDefinition>(json)!;

        var editor = new Views.ProfileEditorWindow(clone, isNew: false);
        editor.Owner = Application.Current.MainWindow;
        if (editor.ShowDialog() == true)
        {
            // Replace in list
            int idx = Profiles.IndexOf(SelectedProfile);
            Profiles[idx] = editor.Profile;
            SelectedProfile = editor.Profile;
            SaveProfileToFile(editor.Profile);
            _main.StatusBarText = $"Updated profile: {editor.Profile.Name}";
        }
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile == null) return;

        var result = MessageBox.Show(
            $"Delete profile '{SelectedProfile.Name}'?",
            "Delete Profile", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        // Remove file
        var devicesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles", "Devices");
        var filePath = Path.Combine(devicesDir, $"{SelectedProfile.Id}.json");
        if (File.Exists(filePath))
            File.Delete(filePath);

        var name = SelectedProfile.Name;
        Profiles.Remove(SelectedProfile);
        SelectedProfile = null;
        _main.StatusBarText = $"Deleted profile: {name}";
    }

    private static void SaveProfileToFile(ProfileDefinition profile)
    {
        var devicesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles", "Devices");
        Directory.CreateDirectory(devicesDir);
        var filePath = Path.Combine(devicesDir, $"{profile.Id}.json");
        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }

    // --- Import/Export ---

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
        if (SelectedProfile == null || !ProfileApplied || ActiveParameterValues.Count == 0) return;

        var controlMapping = SelectedProfile.GetAllMappings().FirstOrDefault(m => m.Direction == "control");
        if (controlMapping == null) return;

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

        var frame = new CanFrame(controlMapping.CanId, data, (byte)data.Length);
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
