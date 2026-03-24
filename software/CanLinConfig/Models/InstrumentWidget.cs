using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CanLinConfig.Models;

public enum WidgetType { Numeric, Bar, Gauge, Boolean, Enum, BitPanel }

public partial class InstrumentWidget : ObservableObject
{
    public string SignalKey { get; set; } = "";
    public string SignalName { get; set; } = "";
    public string Unit { get; set; } = "";
    public WidgetType Type { get; set; } = WidgetType.Numeric;
    public double RangeMin { get; set; }
    public double RangeMax { get; set; } = 100;
    public int BitCount { get; set; } = 8;
    public List<string> BitLabels { get; set; } = [];

    [ObservableProperty] private double _value;
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _minSeen = double.MaxValue;
    [ObservableProperty] private double _maxSeen = double.MinValue;

    public double NormalizedValue
    {
        get
        {
            if (RangeMax <= RangeMin) return 0;
            return Math.Clamp((Value - RangeMin) / (RangeMax - RangeMin), 0, 1);
        }
    }

    public void UpdateValue(double physicalValue)
    {
        Value = physicalValue;
        if (physicalValue < MinSeen) MinSeen = physicalValue;
        if (physicalValue > MaxSeen) MaxSeen = physicalValue;
        OnPropertyChanged(nameof(NormalizedValue));
    }
}

public class WidgetLayout
{
    [JsonPropertyName("signal_key")] public string SignalKey { get; set; } = "";
    [JsonPropertyName("signal_name")] public string SignalName { get; set; } = "";
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "Numeric";
    [JsonPropertyName("range_min")] public double RangeMin { get; set; }
    [JsonPropertyName("range_max")] public double RangeMax { get; set; } = 100;
    [JsonPropertyName("bit_count")] public int BitCount { get; set; } = 8;
    [JsonPropertyName("bit_labels")] public List<string> BitLabels { get; set; } = [];
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
}
