using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Models;

namespace CanLinConfig.ViewModels;

public partial class InstrumentPanelViewModel : ObservableObject
{
    private readonly Dictionary<string, InstrumentWidget> _widgetMap = new();
    public ObservableCollection<InstrumentWidget> Widgets { get; } = [];

    public void AddWidget(string signalKey, string signalName, string unit,
        WidgetType type = WidgetType.Numeric, double rangeMin = 0, double rangeMax = 100)
    {
        if (_widgetMap.ContainsKey(signalKey)) return;
        var widget = new InstrumentWidget
        {
            SignalKey = signalKey, SignalName = signalName, Unit = unit,
            Type = type, RangeMin = rangeMin, RangeMax = rangeMax,
        };
        _widgetMap[signalKey] = widget;
        Widgets.Add(widget);
    }

    [RelayCommand]
    private void RemoveWidget(InstrumentWidget? widget)
    {
        if (widget == null) return;
        _widgetMap.Remove(widget.SignalKey);
        Widgets.Remove(widget);
    }

    public event Action<InstrumentWidget>? RequestBitPanelConfig;

    public void SetWidgetType(InstrumentWidget widget, WidgetType newType)
    {
        if (widget.Type == newType) return;
        widget.Type = newType;
        ForceTemplateRefresh(widget);
    }

    public void ForceTemplateRefresh(InstrumentWidget widget)
    {
        var index = Widgets.IndexOf(widget);
        if (index >= 0)
        {
            Widgets.RemoveAt(index);
            Widgets.Insert(index, widget);
        }
    }

    public void OnSignalValues(IReadOnlyList<SignalValue> values)
    {
        foreach (var sv in values)
        {
            string key = $"{sv.Bus}:{sv.FrameId}:{sv.Name}";
            if (_widgetMap.TryGetValue(key, out var widget))
                widget.UpdateValue(sv.PhysicalValue);
        }
    }

    public IReadOnlyList<WidgetLayout> ToLayouts()
    {
        return Widgets.Select(w => new WidgetLayout
        {
            SignalKey = w.SignalKey, SignalName = w.SignalName, Unit = w.Unit,
            Type = w.Type.ToString(), RangeMin = w.RangeMin, RangeMax = w.RangeMax,
            BitCount = w.BitCount, BitLabels = new List<string>(w.BitLabels),
            X = w.X, Y = w.Y,
        }).ToList();
    }

    public void FromLayouts(IReadOnlyList<WidgetLayout> layouts)
    {
        Widgets.Clear();
        _widgetMap.Clear();
        foreach (var l in layouts)
        {
            var type = System.Enum.TryParse<WidgetType>(l.Type, out var t) ? t : WidgetType.Numeric;
            AddWidget(l.SignalKey, l.SignalName, l.Unit, type, l.RangeMin, l.RangeMax);
            var widget = _widgetMap[l.SignalKey];
            widget.BitCount = l.BitCount;
            widget.BitLabels = l.BitLabels.Count > 0 ? new List<string>(l.BitLabels) : DefaultBitLabels(l.BitCount);
            widget.X = l.X;
            widget.Y = l.Y;
        }
    }

    private static List<string> DefaultBitLabels(int count)
    {
        return Enumerable.Range(0, count).Select(i => $"Bit {i}").ToList();
    }

    [RelayCommand]
    private void ClearAll() { Widgets.Clear(); _widgetMap.Clear(); }
}
