using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Models;
using CanLinConfig.Protocol;

namespace CanLinConfig.ViewModels;

public partial class LinChannelViewModel : ObservableObject
{
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private byte _mode; // 0=Disabled, 1=Master, 2=Slave
    [ObservableProperty] private uint _baudrate = 19200;
    public int ChannelIndex { get; }
    public string ChannelName => $"LIN{ChannelIndex + 1}";

    public ObservableCollection<LinScheduleEntry> Schedule { get; } = [];

    public bool IsMaster => Mode == 1;

    partial void OnModeChanged(byte value) => OnPropertyChanged(nameof(IsMaster));

    public LinChannelViewModel(int index) { ChannelIndex = index; }

    [RelayCommand]
    private void AddEntry()
    {
        if (Schedule.Count < ProtocolConstants.MaxScheduleEntries)
            Schedule.Add(new LinScheduleEntry { DelayMs = 10 });
    }

    [RelayCommand]
    private void RemoveEntry(LinScheduleEntry? entry)
    {
        if (entry != null) Schedule.Remove(entry);
    }

    [RelayCommand]
    private void MoveUp(LinScheduleEntry? entry)
    {
        if (entry == null) return;
        int idx = Schedule.IndexOf(entry);
        if (idx > 0) Schedule.Move(idx, idx - 1);
    }

    [RelayCommand]
    private void MoveDown(LinScheduleEntry? entry)
    {
        if (entry == null) return;
        int idx = Schedule.IndexOf(entry);
        if (idx < Schedule.Count - 1) Schedule.Move(idx, idx + 1);
    }

    // Firmware lin_schedule_table_t layout: count(1) + pad(1) + entries[16]*16 = 258 bytes
    public const int ScheduleTableSize = 2 + ProtocolConstants.MaxScheduleEntries * LinScheduleEntry.PackedSize;
    private const int EntriesOffset = 2; // entries start at offset 2 (1 byte count + 1 byte padding)

    public byte[] SerializeSchedule()
    {
        var buf = new byte[ScheduleTableSize];
        buf[0] = (byte)Schedule.Count;
        // buf[1] = padding (implicit zero)
        for (int i = 0; i < Schedule.Count && i < ProtocolConstants.MaxScheduleEntries; i++)
        {
            var entry = Schedule[i].Serialize();
            Array.Copy(entry, 0, buf, EntriesOffset + i * LinScheduleEntry.PackedSize, LinScheduleEntry.PackedSize);
        }
        return buf;
    }

    public void DeserializeSchedule(byte[] data)
    {
        Schedule.Clear();
        if (data.Length < EntriesOffset) return;
        int count = Math.Min((int)data[0], ProtocolConstants.MaxScheduleEntries);
        for (int i = 0; i < count; i++)
        {
            int offset = EntriesOffset + i * LinScheduleEntry.PackedSize;
            if (offset + LinScheduleEntry.PackedSize <= data.Length)
                Schedule.Add(LinScheduleEntry.Deserialize(data, offset));
        }
    }
}

public partial class LinConfigViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public ObservableCollection<LinChannelViewModel> Channels { get; } = [];
    [ObservableProperty] private int _selectedChannelIndex;

    public LinConfigViewModel(MainViewModel main)
    {
        _main = main;
        for (int i = 0; i < ProtocolConstants.LinChannelCount; i++)
            Channels.Add(new LinChannelViewModel(i));
    }

    public async Task ReadFromDeviceAsync(ConfigProtocol proto)
    {
        for (int ch = 0; ch < ProtocolConstants.LinChannelCount; ch++)
        {
            var vm = Channels[ch];
            var en = await proto.ReadParamAsync(ProtocolConstants.SectionLin, 0, (byte)ch);
            if (en.Success && en.Value.Length >= 1) vm.Enabled = en.Value[0] != 0;

            var mode = await proto.ReadParamAsync(ProtocolConstants.SectionLin, 1, (byte)ch);
            if (mode.Success && mode.Value.Length >= 1) vm.Mode = mode.Value[0];

            var br = await proto.ReadParamAsync(ProtocolConstants.SectionLin, 2, (byte)ch);
            if (br.Success && br.Value.Length >= 3)
                vm.Baudrate = (uint)(br.Value[0] | (br.Value[1] << 8) | (br.Value[2] << 16));

            // Read schedule via bulk read
            var scheduleData = await proto.BulkReadAsync(ProtocolConstants.SectionLin, (byte)ch);
            if (scheduleData != null)
                vm.DeserializeSchedule(scheduleData);
        }
    }

    public async Task WriteToDeviceAsync(ConfigProtocol proto)
    {
        for (int ch = 0; ch < ProtocolConstants.LinChannelCount; ch++)
        {
            var vm = Channels[ch];
            await proto.WriteParamAsync(ProtocolConstants.SectionLin, 0, (byte)ch,
                [(byte)(vm.Enabled ? 1 : 0)]);
            await proto.WriteParamAsync(ProtocolConstants.SectionLin, 1, (byte)ch,
                [vm.Mode]);
            await proto.WriteParamAsync(ProtocolConstants.SectionLin, 2, (byte)ch,
                [(byte)vm.Baudrate, (byte)(vm.Baudrate >> 8), (byte)(vm.Baudrate >> 16)]);

            // Write schedule via bulk write (always send, even if empty, to clear stale data)
            var scheduleData = vm.SerializeSchedule();
            await proto.BulkWriteAsync(ProtocolConstants.SectionLin, (byte)ch, scheduleData);
        }
    }
}
