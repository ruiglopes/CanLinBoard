using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Models;

namespace CanLinConfig.ViewModels;

public partial class SignalPanelViewModel : ObservableObject
{
    private readonly Dictionary<string, SignalEntry> _entryMap = new();

    public ObservableCollection<SignalEntry> Signals { get; } = [];

    public event EventHandler<SignalEntry>? AddToGraphRequested;
    public event EventHandler<SignalEntry>? AddToInstrumentPanelRequested;

    public void UpdateSignals(IReadOnlyList<SignalValue> values)
    {
        foreach (var sv in values)
        {
            string key = $"{sv.Bus}:{sv.FrameId}:{sv.Name}";
            if (_entryMap.TryGetValue(key, out var entry))
            {
                entry.Update(sv);
            }
            else
            {
                entry = new SignalEntry(sv);
                _entryMap[key] = entry;
                Signals.Add(entry);
            }
        }
    }

    [RelayCommand]
    private void AddToGraph(SignalEntry? entry)
    {
        if (entry != null)
            AddToGraphRequested?.Invoke(this, entry);
    }

    [RelayCommand]
    private void AddToInstrumentPanel(SignalEntry? entry)
    {
        if (entry != null)
            AddToInstrumentPanelRequested?.Invoke(this, entry);
    }

    [RelayCommand]
    private void Clear()
    {
        Signals.Clear();
        _entryMap.Clear();
    }
}

public partial class SignalEntry : ObservableObject
{
    public string Name { get; }
    public string Unit { get; }
    public string Bus { get; }
    public uint FrameId { get; }
    public string MessageKey { get; }

    [ObservableProperty] private double _value;
    [ObservableProperty] private double _rawValue;
    [ObservableProperty] private double _minValue = double.MaxValue;
    [ObservableProperty] private double _maxValue = double.MinValue;
    [ObservableProperty] private string _lastUpdate = "";

    public SignalEntry(SignalValue sv)
    {
        Name = sv.Name;
        Unit = sv.Unit;
        Bus = ((BusFrame.Bus)sv.Bus).ToString();
        FrameId = sv.FrameId;
        MessageKey = $"{sv.Bus}:{sv.FrameId}:{sv.Name}";
        Update(sv);
    }

    public void Update(SignalValue sv)
    {
        Value = sv.PhysicalValue;
        RawValue = sv.RawValue;
        if (sv.PhysicalValue < MinValue) MinValue = sv.PhysicalValue;
        if (sv.PhysicalValue > MaxValue) MaxValue = sv.PhysicalValue;
        LastUpdate = sv.Timestamp.ToString("HH:mm:ss.fff");
    }
}
