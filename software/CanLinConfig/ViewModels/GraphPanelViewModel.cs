using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Models;

namespace CanLinConfig.ViewModels;

public partial class GraphPanelViewModel : ObservableObject
{
    private readonly int _maxPoints;

    private readonly Dictionary<string, SignalTrace> _traces = new();

    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private double _timeWindowSeconds = 30.0;

    public ObservableCollection<SignalTrace> Traces { get; } = [];

    public event EventHandler? PlotNeedsRefresh;

    public GraphPanelViewModel(int maxPointsPerSignal = 10_000)
    {
        _maxPoints = maxPointsPerSignal;
    }

    public void AddSignal(string key, string displayName, string unit)
    {
        if (_traces.ContainsKey(key)) return;
        var trace = new SignalTrace(key, displayName, unit, _maxPoints);
        _traces[key] = trace;
        Traces.Add(trace);
    }

    public void RemoveSignal(string key)
    {
        if (_traces.Remove(key, out var trace))
            Traces.Remove(trace);
    }

    public void OnSignalValues(IReadOnlyList<SignalValue> values)
    {
        if (IsPaused) return;

        bool anyUpdated = false;
        foreach (var sv in values)
        {
            string key = $"{sv.Bus}:{sv.FrameId}:{sv.Name}";
            if (_traces.TryGetValue(key, out var trace))
            {
                trace.AddPoint(sv.Timestamp, sv.PhysicalValue);
                anyUpdated = true;
            }
        }

        if (anyUpdated)
            PlotNeedsRefresh?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand]
    private void ClearAll()
    {
        foreach (var trace in _traces.Values)
            trace.Clear();
        PlotNeedsRefresh?.Invoke(this, EventArgs.Empty);
    }
}

public class SignalTrace
{
    public string Key { get; }
    public string DisplayName { get; }
    public string Unit { get; }
    private readonly int _maxPoints;

    private readonly object _lock = new();
    private readonly List<double> _timestamps = new();
    private readonly List<double> _values = new();

    public SignalTrace(string key, string displayName, string unit, int maxPoints)
    {
        Key = key;
        DisplayName = displayName;
        Unit = unit;
        _maxPoints = maxPoints;
    }

    public void AddPoint(DateTime timestamp, double value)
    {
        lock (_lock)
        {
            _timestamps.Add(timestamp.ToOADate());
            _values.Add(value);
            while (_timestamps.Count > _maxPoints)
            {
                _timestamps.RemoveAt(0);
                _values.RemoveAt(0);
            }
        }
    }

    public (double[] timestamps, double[] values) GetData()
    {
        lock (_lock)
            return (_timestamps.ToArray(), _values.ToArray());
    }

    public void Clear()
    {
        lock (_lock)
        {
            _timestamps.Clear();
            _values.Clear();
        }
    }
}
