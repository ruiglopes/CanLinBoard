using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Models;

namespace CanLinConfig.ViewModels;

public partial class TracePanelViewModel : ObservableObject
{
    private readonly int _maxEntries;

    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _idFilter = "";
    [ObservableProperty] private string _busFilter = "All";

    public ObservableCollection<TraceEntry> Entries { get; } = [];

    public TracePanelViewModel(int maxEntries = 10_000)
    {
        _maxEntries = maxEntries;
    }

    public void AddFrame(BusFrame frame, string? messageName)
    {
        if (IsPaused) return;

        var entry = new TraceEntry(frame, messageName);
        if (!PassesFilter(entry)) return;

        Entries.Add(entry);
        while (Entries.Count > _maxEntries)
            Entries.RemoveAt(0);
    }

    private bool PassesFilter(TraceEntry entry)
    {
        if (!string.IsNullOrEmpty(IdFilter))
        {
            var filterText = IdFilter.Replace("0x", "").Replace("0X", "");
            if (uint.TryParse(filterText, System.Globalization.NumberStyles.HexNumber, null, out uint filterId))
            {
                if (entry.RawId != filterId) return false;
            }
        }
        if (BusFilter != "All" && entry.Bus != BusFilter)
            return false;
        return true;
    }

    [RelayCommand]
    private void Clear() => Entries.Clear();

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;
}

public class TraceEntry
{
    public string Time { get; }
    public string Bus { get; }
    public string Id { get; }
    public uint RawId { get; }
    public byte Dlc { get; }
    public string DataHex { get; }
    public string? MessageName { get; }

    public TraceEntry(BusFrame frame, string? messageName)
    {
        Time = frame.Timestamp.ToString("HH:mm:ss.fff");
        Bus = frame.BusName;
        Id = $"0x{frame.Id:X3}";
        RawId = frame.Id;
        Dlc = frame.Dlc;
        DataHex = string.Join(" ", frame.Data.Take(frame.Dlc).Select(b => b.ToString("X2")));
        MessageName = messageName;
    }
}
