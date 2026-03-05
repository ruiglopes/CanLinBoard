using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Protocol;

namespace CanLinConfig.ViewModels;

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

public partial class ProfilesViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public ObservableCollection<ProfileDefinition> Profiles { get; } = [];
    [ObservableProperty] private ProfileDefinition? _selectedProfile;
    [ObservableProperty] private int _assignedChannel; // 0-3

    // Profile enable params (firmware)
    [ObservableProperty] private bool _wdaEnabled;
    [ObservableProperty] private byte _wdaChannel;
    [ObservableProperty] private bool _cwa400Enabled;
    [ObservableProperty] private byte _cwa400Channel;

    public ProfilesViewModel(MainViewModel main)
    {
        _main = main;
        LoadProfiles();
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
                linVm.Schedule.Add(new Models.LinScheduleEntry
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

        // Switch to LIN tab showing the affected channel
        _main.LinConfig.SelectedChannelIndex = ch;

        _main.StatusBarText = $"Profile '{SelectedProfile.Name}' applied to LIN{ch + 1}. Click Save NVM to persist.";
    }

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
