# Instrument Panel — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an INCA-style instrument panel to the Bus Monitor tab where users can drag signals into configurable widgets (numeric displays, bar meters, gauges, boolean indicators) for live monitoring.

**Architecture:** An `InstrumentPanelViewModel` manages a collection of `InstrumentWidget` items, each bound to a signal key from `BusDataService`. Widgets are self-contained UserControls rendered in a `WrapPanel`. Users add signals via right-click "Add to Instrument Panel" in the SignalPanel. Widget layouts are serializable for project save/load.

**Tech Stack:** .NET 8, WPF custom controls (no external gauge libraries), MahApps.Metro dark theme

**Spec:** `docs/superpowers/specs/2026-03-23-bus-monitor-logger-design.md` (Section 4 — InstrumentPanel)

**Important:** Do NOT commit specs or plans. Only commit actual code changes, and only after the feature is completed and tested.

---

## File Map

### New Files

| File | Responsibility |
|------|---------------|
| `software/CanLinConfig/Models/InstrumentWidget.cs` | Widget data model (type, signal key, display config, range) |
| `software/CanLinConfig/ViewModels/InstrumentPanelViewModel.cs` | Manages widget collection, routes signal updates to widgets |
| `software/CanLinConfig/Views/Widgets/NumericWidget.xaml` + `.cs` | Large numeric value display with unit and min/max |
| `software/CanLinConfig/Views/Widgets/BarWidget.xaml` + `.cs` | Horizontal/vertical fill bar with range |
| `software/CanLinConfig/Views/Widgets/GaugeWidget.xaml` + `.cs` | Radial arc gauge with needle |
| `software/CanLinConfig/Views/Widgets/BooleanWidget.xaml` + `.cs` | On/off indicator lamp |
| `software/CanLinConfig/Views/Widgets/EnumWidget.xaml` + `.cs` | Text state display |
| `software/CanLinConfig/Views/InstrumentPanel.xaml` + `.cs` | WrapPanel host for widgets with add/config/remove |
| `software/CanLinConfig.Tests/InstrumentPanelViewModelTests.cs` | Widget management and signal routing tests |

### Modified Files

| File | Changes |
|------|---------|
| `software/CanLinConfig/ViewModels/BusMonitorViewModel.cs` | Add InstrumentPanelViewModel, wire signals, add "Add to Instrument Panel" |
| `software/CanLinConfig/ViewModels/SignalPanelViewModel.cs` | Add "Add to Instrument Panel" event |
| `software/CanLinConfig/Views/SignalPanel.xaml.cs` | Add context menu item for instrument panel |
| `software/CanLinConfig/Views/BusMonitorView.xaml` | Add InstrumentPanel as fourth panel (tabbed with Graph) |
| `software/CanLinConfig/Models/Project.cs` | Add instrument layout to ProjectManifest |
| `software/CanLinConfig/Services/ProjectService.cs` | Save/load instrument layout |

---

## Task 1: InstrumentWidget Model + InstrumentPanelViewModel (TDD)

**Files:**
- Create: `software/CanLinConfig/Models/InstrumentWidget.cs`
- Create: `software/CanLinConfig/ViewModels/InstrumentPanelViewModel.cs`
- Create: `software/CanLinConfig.Tests/InstrumentPanelViewModelTests.cs`

- [ ] **Step 1: Create InstrumentWidget model**

```csharp
// software/CanLinConfig/Models/InstrumentWidget.cs
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CanLinConfig.Models;

public enum WidgetType
{
    Numeric,
    Bar,
    Gauge,
    Boolean,
    Enum
}

/// <summary>
/// Represents a single instrument widget bound to a signal.
/// Observable for live value updates from BusDataService.
/// </summary>
public partial class InstrumentWidget : ObservableObject
{
    // Configuration (set once, serialized)
    public string SignalKey { get; set; } = "";   // "Bus:MsgId:Name"
    public string SignalName { get; set; } = "";
    public string Unit { get; set; } = "";
    public WidgetType Type { get; set; } = WidgetType.Numeric;
    public double RangeMin { get; set; }
    public double RangeMax { get; set; } = 100;

    // Live values (updated from signal stream)
    [ObservableProperty] private double _value;
    [ObservableProperty] private double _minSeen = double.MaxValue;
    [ObservableProperty] private double _maxSeen = double.MinValue;
    [ObservableProperty] private string _enumText = "";

    /// <summary>Normalized value 0.0–1.0 for bar/gauge rendering.</summary>
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

/// <summary>
/// Serializable widget layout for project save/load.
/// </summary>
public class WidgetLayout
{
    [JsonPropertyName("signal_key")] public string SignalKey { get; set; } = "";
    [JsonPropertyName("signal_name")] public string SignalName { get; set; } = "";
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "Numeric";
    [JsonPropertyName("range_min")] public double RangeMin { get; set; }
    [JsonPropertyName("range_max")] public double RangeMax { get; set; } = 100;
}
```

- [ ] **Step 2: Write failing tests**

```csharp
// software/CanLinConfig.Tests/InstrumentPanelViewModelTests.cs
using CanLinConfig.Models;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Tests;

public class InstrumentPanelViewModelTests
{
    [Fact]
    public void AddWidget_adds_to_collection()
    {
        var vm = new InstrumentPanelViewModel();
        vm.AddWidget("0:256:EngineRPM", "EngineRPM", "rpm", WidgetType.Numeric, 0, 8000);

        Assert.Single(vm.Widgets);
        Assert.Equal("EngineRPM", vm.Widgets[0].SignalName);
        Assert.Equal(WidgetType.Numeric, vm.Widgets[0].Type);
    }

    [Fact]
    public void AddWidget_prevents_duplicate_signal()
    {
        var vm = new InstrumentPanelViewModel();
        vm.AddWidget("0:256:EngineRPM", "EngineRPM", "rpm", WidgetType.Numeric, 0, 8000);
        vm.AddWidget("0:256:EngineRPM", "EngineRPM", "rpm", WidgetType.Bar, 0, 8000);

        Assert.Single(vm.Widgets); // no duplicate
    }

    [Fact]
    public void RemoveWidget_removes_from_collection()
    {
        var vm = new InstrumentPanelViewModel();
        vm.AddWidget("0:256:EngineRPM", "EngineRPM", "rpm", WidgetType.Numeric, 0, 8000);
        vm.RemoveWidgetCommand.Execute(vm.Widgets[0]);

        Assert.Empty(vm.Widgets);
    }

    [Fact]
    public void OnSignalValues_updates_matching_widget()
    {
        var vm = new InstrumentPanelViewModel();
        vm.AddWidget("0:256:EngineRPM", "EngineRPM", "rpm", WidgetType.Numeric, 0, 8000);

        var signals = new List<SignalValue>
        {
            new("EngineRPM", 1000, 250.0, "rpm", 0, 256, DateTime.Now),
            new("CoolantTemp", 200, 160.0, "C", 0, 256, DateTime.Now),
        };
        vm.OnSignalValues(signals);

        Assert.Equal(250.0, vm.Widgets[0].Value);
    }

    [Fact]
    public void OnSignalValues_ignores_unmatched_signals()
    {
        var vm = new InstrumentPanelViewModel();
        vm.AddWidget("0:256:EngineRPM", "EngineRPM", "rpm", WidgetType.Numeric, 0, 8000);

        var signals = new List<SignalValue>
        {
            new("CoolantTemp", 200, 160.0, "C", 0, 256, DateTime.Now),
        };
        vm.OnSignalValues(signals);

        Assert.Equal(0.0, vm.Widgets[0].Value); // unchanged
    }

    [Fact]
    public void NormalizedValue_clamps_to_0_1()
    {
        var widget = new InstrumentWidget
        {
            RangeMin = 0, RangeMax = 100
        };
        widget.UpdateValue(50);
        Assert.Equal(0.5, widget.NormalizedValue);

        widget.UpdateValue(150);
        Assert.Equal(1.0, widget.NormalizedValue);

        widget.UpdateValue(-10);
        Assert.Equal(0.0, widget.NormalizedValue);
    }

    [Fact]
    public void ToLayouts_and_FromLayouts_round_trip()
    {
        var vm = new InstrumentPanelViewModel();
        vm.AddWidget("0:256:EngineRPM", "EngineRPM", "rpm", WidgetType.Gauge, 0, 8000);
        vm.AddWidget("0:256:CoolantTemp", "CoolantTemp", "C", WidgetType.Bar, -40, 215);

        var layouts = vm.ToLayouts();
        Assert.Equal(2, layouts.Count);

        var vm2 = new InstrumentPanelViewModel();
        vm2.FromLayouts(layouts);
        Assert.Equal(2, vm2.Widgets.Count);
        Assert.Equal("EngineRPM", vm2.Widgets[0].SignalName);
        Assert.Equal(WidgetType.Gauge, vm2.Widgets[0].Type);
        Assert.Equal(8000, vm2.Widgets[0].RangeMax);
    }
}
```

- [ ] **Step 3: Implement InstrumentPanelViewModel**

```csharp
// software/CanLinConfig/ViewModels/InstrumentPanelViewModel.cs
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
            SignalKey = signalKey,
            SignalName = signalName,
            Unit = unit,
            Type = type,
            RangeMin = rangeMin,
            RangeMax = rangeMax,
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

    [RelayCommand]
    private void CycleWidgetType(InstrumentWidget? widget)
    {
        if (widget == null) return;
        widget.Type = widget.Type switch
        {
            WidgetType.Numeric => WidgetType.Bar,
            WidgetType.Bar => WidgetType.Gauge,
            WidgetType.Gauge => WidgetType.Boolean,
            WidgetType.Boolean => WidgetType.Enum,
            WidgetType.Enum => WidgetType.Numeric,
            _ => WidgetType.Numeric
        };
        OnPropertyChanged(nameof(Widgets)); // trigger re-render
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
            SignalKey = w.SignalKey,
            SignalName = w.SignalName,
            Unit = w.Unit,
            Type = w.Type.ToString(),
            RangeMin = w.RangeMin,
            RangeMax = w.RangeMax,
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
        }
    }

    [RelayCommand]
    private void ClearAll()
    {
        Widgets.Clear();
        _widgetMap.Clear();
    }
}
```

- [ ] **Step 4: Run tests**

```bash
cd software && dotnet test CanLinConfig.Tests --filter "InstrumentPanelViewModelTests" -v n
```

Expected: 7 tests PASS.

- [ ] **Step 5: Commit**

```
feat: add InstrumentWidget model and InstrumentPanelViewModel
```

---

## Task 2: Widget UserControls

Five widget types, each a self-contained UserControl styled for MahApps.Metro dark theme.

**Files:**
- Create: `software/CanLinConfig/Views/Widgets/NumericWidget.xaml` + `.cs`
- Create: `software/CanLinConfig/Views/Widgets/BarWidget.xaml` + `.cs`
- Create: `software/CanLinConfig/Views/Widgets/GaugeWidget.xaml` + `.cs`
- Create: `software/CanLinConfig/Views/Widgets/BooleanWidget.xaml` + `.cs`
- Create: `software/CanLinConfig/Views/Widgets/EnumWidget.xaml` + `.cs`

- [ ] **Step 1: Create NumericWidget**

```xml
<!-- software/CanLinConfig/Views/Widgets/NumericWidget.xaml -->
<UserControl x:Class="CanLinConfig.Views.Widgets.NumericWidget"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Width="180" Height="100">
    <Border BorderBrush="#555" BorderThickness="1" CornerRadius="4" Padding="8" Background="#1E1E1E">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>
            <TextBlock Grid.Row="0" Text="{Binding SignalName}" FontSize="11"
                       Foreground="#AAA" TextTrimming="CharacterEllipsis"/>
            <StackPanel Grid.Row="1" Orientation="Horizontal" HorizontalAlignment="Center" VerticalAlignment="Center">
                <TextBlock Text="{Binding Value, StringFormat=F1}" FontSize="28" FontWeight="Bold" Foreground="White"/>
                <TextBlock Text="{Binding Unit}" FontSize="14" Foreground="#888" VerticalAlignment="Bottom" Margin="4,0,0,4"/>
            </StackPanel>
            <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Center">
                <TextBlock Text="Min:" FontSize="10" Foreground="#666" Margin="0,0,2,0"/>
                <TextBlock Text="{Binding MinSeen, StringFormat=F1}" FontSize="10" Foreground="#666" Margin="0,0,8,0"/>
                <TextBlock Text="Max:" FontSize="10" Foreground="#666" Margin="0,0,2,0"/>
                <TextBlock Text="{Binding MaxSeen, StringFormat=F1}" FontSize="10" Foreground="#666"/>
            </StackPanel>
        </Grid>
    </Border>
</UserControl>
```

```csharp
using System.Windows.Controls;
namespace CanLinConfig.Views.Widgets;
public partial class NumericWidget : UserControl
{
    public NumericWidget() { InitializeComponent(); }
}
```

- [ ] **Step 2: Create BarWidget**

```xml
<!-- software/CanLinConfig/Views/Widgets/BarWidget.xaml -->
<UserControl x:Class="CanLinConfig.Views.Widgets.BarWidget"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Width="180" Height="100">
    <Border BorderBrush="#555" BorderThickness="1" CornerRadius="4" Padding="8" Background="#1E1E1E">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>
            <TextBlock Grid.Row="0" Text="{Binding SignalName}" FontSize="11" Foreground="#AAA" TextTrimming="CharacterEllipsis"/>
            <TextBlock Grid.Row="1" HorizontalAlignment="Center" Margin="0,2">
                <Run Text="{Binding Value, StringFormat=F1, Mode=OneWay}" FontSize="16" FontWeight="Bold" Foreground="White"/>
                <Run Text="{Binding Unit, Mode=OneWay}" FontSize="11" Foreground="#888"/>
            </TextBlock>
            <Grid Grid.Row="2" Margin="0,4" MinHeight="16">
                <Border Background="#333" CornerRadius="2"/>
                <Border Background="#0078D4" CornerRadius="2" HorizontalAlignment="Left">
                    <Border.Width>
                        <MultiBinding Converter="{x:Null}" FallbackValue="0">
                            <!-- Width set in code-behind -->
                        </MultiBinding>
                    </Border.Width>
                </Border>
            </Grid>
            <StackPanel Grid.Row="3" Orientation="Horizontal" HorizontalAlignment="Stretch">
                <TextBlock Text="{Binding RangeMin, StringFormat=F0}" FontSize="9" Foreground="#555"/>
                <TextBlock Text="{Binding RangeMax, StringFormat=F0}" FontSize="9" Foreground="#555" HorizontalAlignment="Right"/>
            </StackPanel>
        </Grid>
    </Border>
</UserControl>
```

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CanLinConfig.Models;

namespace CanLinConfig.Views.Widgets;

public partial class BarWidget : UserControl
{
    public BarWidget()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is InstrumentWidget oldW)
            oldW.PropertyChanged -= OnWidgetPropertyChanged;
        if (e.NewValue is InstrumentWidget newW)
            newW.PropertyChanged += OnWidgetPropertyChanged;
    }

    private void OnWidgetPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstrumentWidget.NormalizedValue))
            UpdateBar();
    }

    private void UpdateBar()
    {
        // Bar fill is handled via code-behind since MultiBinding without converter is complex
        // The XAML bar fill border will be updated here if needed
    }
}
```

Actually, the bar fill is simpler done entirely in XAML with a ScaleTransform. Let me simplify.

Replace the BarWidget XAML with:

```xml
<!-- software/CanLinConfig/Views/Widgets/BarWidget.xaml -->
<UserControl x:Class="CanLinConfig.Views.Widgets.BarWidget"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Width="180" Height="100">
    <Border BorderBrush="#555" BorderThickness="1" CornerRadius="4" Padding="8" Background="#1E1E1E">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>
            <TextBlock Grid.Row="0" Text="{Binding SignalName}" FontSize="11" Foreground="#AAA"/>
            <TextBlock Grid.Row="1" HorizontalAlignment="Center" Margin="0,2">
                <Run Text="{Binding Value, StringFormat=F1, Mode=OneWay}" FontSize="16" FontWeight="Bold" Foreground="White"/>
                <Run Text="{Binding Unit, Mode=OneWay}" FontSize="11" Foreground="#888"/>
            </TextBlock>
            <Border Grid.Row="2" Background="#333" CornerRadius="2" Margin="0,4" MinHeight="16">
                <Border Background="#0078D4" CornerRadius="2" HorizontalAlignment="Left"
                        RenderTransformOrigin="0,0.5">
                    <Border.RenderTransform>
                        <ScaleTransform ScaleX="{Binding NormalizedValue}" ScaleY="1"/>
                    </Border.RenderTransform>
                </Border>
            </Border>
            <Grid Grid.Row="3">
                <TextBlock Text="{Binding RangeMin, StringFormat=F0}" FontSize="9" Foreground="#555" HorizontalAlignment="Left"/>
                <TextBlock Text="{Binding RangeMax, StringFormat=F0}" FontSize="9" Foreground="#555" HorizontalAlignment="Right"/>
            </Grid>
        </Grid>
    </Border>
</UserControl>
```

Simple code-behind:
```csharp
using System.Windows.Controls;
namespace CanLinConfig.Views.Widgets;
public partial class BarWidget : UserControl
{
    public BarWidget() { InitializeComponent(); }
}
```

- [ ] **Step 3: Create GaugeWidget**

A simple radial gauge using WPF Path with an arc and a needle line.

```xml
<!-- software/CanLinConfig/Views/Widgets/GaugeWidget.xaml -->
<UserControl x:Class="CanLinConfig.Views.Widgets.GaugeWidget"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Width="180" Height="160">
    <Border BorderBrush="#555" BorderThickness="1" CornerRadius="4" Padding="8" Background="#1E1E1E">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>
            <TextBlock Grid.Row="0" Text="{Binding SignalName}" FontSize="11" Foreground="#AAA" HorizontalAlignment="Center"/>
            <Canvas Grid.Row="1" x:Name="GaugeCanvas" Width="140" Height="80" Margin="0,4"/>
            <TextBlock Grid.Row="2" HorizontalAlignment="Center">
                <Run Text="{Binding Value, StringFormat=F1, Mode=OneWay}" FontSize="16" FontWeight="Bold" Foreground="White"/>
                <Run Text="{Binding Unit, Mode=OneWay}" FontSize="11" Foreground="#888"/>
            </TextBlock>
        </Grid>
    </Border>
</UserControl>
```

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CanLinConfig.Models;

namespace CanLinConfig.Views.Widgets;

public partial class GaugeWidget : UserControl
{
    private Line? _needle;

    public GaugeWidget()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DrawGauge();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is InstrumentWidget oldW)
            oldW.PropertyChanged -= OnWidgetChanged;
        if (e.NewValue is InstrumentWidget newW)
            newW.PropertyChanged += OnWidgetChanged;
    }

    private void OnWidgetChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstrumentWidget.NormalizedValue))
            UpdateNeedle();
    }

    private void DrawGauge()
    {
        GaugeCanvas.Children.Clear();
        double cx = 70, cy = 70, r = 60;

        // Arc background (220 degrees, from -200 to 20)
        var arcPath = new Path
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            StrokeThickness = 8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        var fig = new PathFigure { StartPoint = PointOnArc(cx, cy, r, -200) };
        fig.Segments.Add(new ArcSegment
        {
            Point = PointOnArc(cx, cy, r, 20),
            Size = new Size(r, r),
            IsLargeArc = true,
            SweepDirection = SweepDirection.Clockwise,
        });
        arcPath.Data = new PathGeometry(new[] { fig });
        GaugeCanvas.Children.Add(arcPath);

        // Needle
        _needle = new Line
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44)),
            StrokeThickness = 2,
            X1 = cx, Y1 = cy,
        };
        GaugeCanvas.Children.Add(_needle);

        // Center dot
        var dot = new Ellipse
        {
            Width = 8, Height = 8,
            Fill = new SolidColorBrush(Colors.White),
        };
        Canvas.SetLeft(dot, cx - 4);
        Canvas.SetTop(dot, cy - 4);
        GaugeCanvas.Children.Add(dot);

        UpdateNeedle();
    }

    private void UpdateNeedle()
    {
        if (_needle == null || DataContext is not InstrumentWidget w) return;
        double cx = 70, cy = 70, r = 50;
        // Map normalized 0-1 to angle -200 to 20
        double angle = -200 + w.NormalizedValue * 220;
        var pt = PointOnArc(cx, cy, r, angle);
        _needle.X2 = pt.X;
        _needle.Y2 = pt.Y;
    }

    private static Point PointOnArc(double cx, double cy, double r, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180;
        return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }
}
```

- [ ] **Step 4: Create BooleanWidget**

```xml
<!-- software/CanLinConfig/Views/Widgets/BooleanWidget.xaml -->
<UserControl x:Class="CanLinConfig.Views.Widgets.BooleanWidget"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Width="120" Height="80">
    <Border BorderBrush="#555" BorderThickness="1" CornerRadius="4" Padding="8" Background="#1E1E1E">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
            </Grid.RowDefinitions>
            <TextBlock Grid.Row="0" Text="{Binding SignalName}" FontSize="11" Foreground="#AAA" HorizontalAlignment="Center"/>
            <Ellipse Grid.Row="1" Width="30" Height="30" HorizontalAlignment="Center" VerticalAlignment="Center">
                <Ellipse.Style>
                    <Style TargetType="Ellipse">
                        <Setter Property="Fill" Value="#444"/>
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding Value, Converter={x:Null}, FallbackValue=0}" Value="0">
                                <Setter Property="Fill" Value="#444"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </Ellipse.Style>
            </Ellipse>
        </Grid>
    </Border>
</UserControl>
```

The boolean lamp color is simpler in code-behind:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CanLinConfig.Models;

namespace CanLinConfig.Views.Widgets;

public partial class BooleanWidget : UserControl
{
    private Ellipse? _lamp;

    public BooleanWidget()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Find the ellipse
            _lamp = FindVisualChild<Ellipse>(this);
            UpdateLamp();
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is InstrumentWidget oldW) oldW.PropertyChanged -= OnChanged;
            if (e.NewValue is InstrumentWidget newW) newW.PropertyChanged += OnChanged;
        };
    }

    private void OnChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstrumentWidget.Value)) UpdateLamp();
    }

    private void UpdateLamp()
    {
        if (_lamp == null || DataContext is not InstrumentWidget w) return;
        _lamp.Fill = w.Value != 0
            ? new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x00))
            : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }
}
```

- [ ] **Step 5: Create EnumWidget**

```xml
<!-- software/CanLinConfig/Views/Widgets/EnumWidget.xaml -->
<UserControl x:Class="CanLinConfig.Views.Widgets.EnumWidget"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Width="140" Height="80">
    <Border BorderBrush="#555" BorderThickness="1" CornerRadius="4" Padding="8" Background="#1E1E1E">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
            </Grid.RowDefinitions>
            <TextBlock Grid.Row="0" Text="{Binding SignalName}" FontSize="11" Foreground="#AAA" HorizontalAlignment="Center"/>
            <TextBlock Grid.Row="1" Text="{Binding Value, StringFormat=F0}" FontSize="20" FontWeight="Bold"
                       Foreground="#0078D4" HorizontalAlignment="Center" VerticalAlignment="Center"/>
        </Grid>
    </Border>
</UserControl>
```

```csharp
using System.Windows.Controls;
namespace CanLinConfig.Views.Widgets;
public partial class EnumWidget : UserControl
{
    public EnumWidget() { InitializeComponent(); }
}
```

- [ ] **Step 6: Verify build**

```bash
cd software && dotnet build CanLinConfig/CanLinConfig.csproj
```

- [ ] **Step 7: Commit**

```
feat: add instrument widget UserControls (numeric, bar, gauge, boolean, enum)
```

---

## Task 3: InstrumentPanel View + Wire into Bus Monitor

**Files:**
- Create: `software/CanLinConfig/Views/InstrumentPanel.xaml` + `.cs`
- Modify: `software/CanLinConfig/ViewModels/BusMonitorViewModel.cs`
- Modify: `software/CanLinConfig/ViewModels/SignalPanelViewModel.cs`
- Modify: `software/CanLinConfig/Views/SignalPanel.xaml.cs`
- Modify: `software/CanLinConfig/Views/BusMonitorView.xaml`

- [ ] **Step 1: Create InstrumentPanel view**

The InstrumentPanel hosts widgets in a WrapPanel. A DataTemplateSelector chooses the correct widget type based on `WidgetType`.

```xml
<!-- software/CanLinConfig/Views/InstrumentPanel.xaml -->
<UserControl x:Class="CanLinConfig.Views.InstrumentPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:widgets="clr-namespace:CanLinConfig.Views.Widgets"
             xmlns:models="clr-namespace:CanLinConfig.Models">
    <UserControl.Resources>
        <DataTemplate x:Key="NumericTemplate" DataType="{x:Type models:InstrumentWidget}">
            <widgets:NumericWidget DataContext="{Binding}"/>
        </DataTemplate>
        <DataTemplate x:Key="BarTemplate" DataType="{x:Type models:InstrumentWidget}">
            <widgets:BarWidget DataContext="{Binding}"/>
        </DataTemplate>
        <DataTemplate x:Key="GaugeTemplate" DataType="{x:Type models:InstrumentWidget}">
            <widgets:GaugeWidget DataContext="{Binding}"/>
        </DataTemplate>
        <DataTemplate x:Key="BooleanTemplate" DataType="{x:Type models:InstrumentWidget}">
            <widgets:BooleanWidget DataContext="{Binding}"/>
        </DataTemplate>
        <DataTemplate x:Key="EnumTemplate" DataType="{x:Type models:InstrumentWidget}">
            <widgets:EnumWidget DataContext="{Binding}"/>
        </DataTemplate>
    </UserControl.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <ToolBar Grid.Row="0">
            <Button Content="Clear All" Command="{Binding ClearAllCommand}"/>
        </ToolBar>

        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
            <ItemsControl ItemsSource="{Binding Widgets}">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate>
                        <WrapPanel/>
                    </ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <ContentControl Content="{Binding}" Margin="4">
                            <ContentControl.ContentTemplate>
                                <!-- Template selected in code-behind via TemplateSelector -->
                                <DataTemplate>
                                    <ContentPresenter Content="{Binding}"/>
                                </DataTemplate>
                            </ContentControl.ContentTemplate>
                            <ContentControl.ContextMenu>
                                <ContextMenu>
                                    <MenuItem Header="Change Type" Click="OnCycleType"/>
                                    <MenuItem Header="Remove" Click="OnRemoveWidget"/>
                                </ContextMenu>
                            </ContentControl.ContextMenu>
                        </ContentControl>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>
    </Grid>
</UserControl>
```

```csharp
// software/CanLinConfig/Views/InstrumentPanel.xaml.cs
using System.Windows;
using System.Windows.Controls;
using CanLinConfig.Models;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Views;

public partial class InstrumentPanel : UserControl
{
    public InstrumentPanel()
    {
        InitializeComponent();
    }

    private void OnCycleType(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is InstrumentWidget widget
            && DataContext is InstrumentPanelViewModel vm)
        {
            vm.CycleWidgetTypeCommand.Execute(widget);
        }
    }

    private void OnRemoveWidget(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is InstrumentWidget widget
            && DataContext is InstrumentPanelViewModel vm)
        {
            vm.RemoveWidgetCommand.Execute(widget);
        }
    }
}
```

**Note for implementer:** The DataTemplate selection by widget type is tricky. The simplest approach is to use a `DataTemplateSelector` in the ItemsControl, or handle it in the InstrumentPanel code-behind. Read the actual XAML after creation to verify it renders correctly. If `ContentControl` with template doesn't auto-select by type, implement a `WidgetTemplateSelector`:

```csharp
public class WidgetTemplateSelector : DataTemplateSelector
{
    public DataTemplate? NumericTemplate { get; set; }
    public DataTemplate? BarTemplate { get; set; }
    public DataTemplate? GaugeTemplate { get; set; }
    public DataTemplate? BooleanTemplate { get; set; }
    public DataTemplate? EnumTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not InstrumentWidget w) return base.SelectTemplate(item, container);
        return w.Type switch
        {
            WidgetType.Numeric => NumericTemplate,
            WidgetType.Bar => BarTemplate,
            WidgetType.Gauge => GaugeTemplate,
            WidgetType.Boolean => BooleanTemplate,
            WidgetType.Enum => EnumTemplate,
            _ => NumericTemplate
        };
    }
}
```

- [ ] **Step 2: Add InstrumentPanel to BusMonitorViewModel**

Add to `BusMonitorViewModel.cs`:

```csharp
public InstrumentPanelViewModel Instruments { get; }
```

In constructor:
```csharp
Instruments = new InstrumentPanelViewModel();
```

Add to `OnSignalsDecoded`:
```csharp
Instruments.OnSignalValues(signals);
```

Add event handler for "Add to Instrument Panel":
```csharp
Signals.AddToInstrumentPanelRequested += OnAddToInstrumentPanel;
```

```csharp
private void OnAddToInstrumentPanel(object? sender, SignalEntry entry)
{
    Instruments.AddWidget(entry.MessageKey, entry.Name, entry.Unit, WidgetType.Numeric,
        0, 100); // default range, user can adjust later
}
```

- [ ] **Step 3: Add "Add to Instrument Panel" to SignalPanel**

In `SignalPanelViewModel.cs`, add:
```csharp
public event EventHandler<SignalEntry>? AddToInstrumentPanelRequested;

[RelayCommand]
private void AddToInstrumentPanel(SignalEntry? entry)
{
    if (entry != null)
        AddToInstrumentPanelRequested?.Invoke(this, entry);
}
```

In `SignalPanel.xaml.cs`, add a second context menu item:
```csharp
var addToInstrument = new MenuItem { Header = "Add to Instrument Panel" };
addToInstrument.Click += (_, _) =>
{
    if (DataContext is SignalPanelViewModel vm && SignalGrid.SelectedItem is SignalEntry entry)
        vm.AddToInstrumentPanelCommand.Execute(entry);
};
menu.Items.Add(addToInstrument);
```

- [ ] **Step 4: Add InstrumentPanel to BusMonitorView.xaml**

The instrument panel goes alongside the graph panel. Use a TabControl in the bottom section so the user can switch between Graph and Instruments:

Replace the bottom panel section in `BusMonitorView.xaml`:

```xml
<!-- Bottom section: Graph + Instruments in tabs -->
<TabControl Grid.Row="2" Grid.Column="0" Grid.ColumnSpan="3">
    <TabItem Header="Graph">
        <local:GraphPanel DataContext="{Binding Graph}"/>
    </TabItem>
    <TabItem Header="Instruments">
        <local:InstrumentPanel DataContext="{Binding Instruments}"/>
    </TabItem>
</TabControl>
```

- [ ] **Step 5: Verify build and all tests**

```bash
cd software && dotnet build CanLinConfig.sln && dotnet test CanLinConfig.Tests -v quiet
```

- [ ] **Step 6: Commit**

```
feat: add InstrumentPanel view with widget rendering and signal wiring
```

---

## Task 4: Project System Integration + Test Guide

Wire instrument layouts into project save/load and update the test guide.

**Files:**
- Modify: `software/CanLinConfig/Models/Project.cs`
- Modify: `software/CanLinConfig/ViewModels/MainViewModel.cs`
- Modify: `software/TEST_GUIDE.md`

- [ ] **Step 1: Add instruments to ProjectManifest**

In `Project.cs`, add to `ProjectManifest`:
```csharp
[JsonPropertyName("instruments")]
public List<WidgetLayout> Instruments { get; set; } = [];
```

- [ ] **Step 2: Save/load instruments in MainViewModel**

In `CaptureCurrentState()` or alongside `SaveProject`, capture instrument layouts:
Read MainViewModel to see how the project save flow works. The instrument layouts should be saved to `project.Manifest.Instruments` from `BusMonitor.Instruments.ToLayouts()`.

In `ApplyState()` or after opening a project, restore:
```csharp
BusMonitor.Instruments.FromLayouts(project.Manifest.Instruments);
```

- [ ] **Step 3: Update test guide**

Add to `software/TEST_GUIDE.md` before "Known Limitations":

```markdown
---

## Instrument Panel Tests (Plan 7 — No Hardware Required)

### Automated Unit Tests

| Test Class | Count | What it covers |
|------------|-------|----------------|
| InstrumentPanelViewModelTests | 7 | Add/remove widget, duplicate prevention, signal routing, normalized value, layout round-trip |

### Instrument Panel Manual Tests

#### INS-1: Add signal to instrument panel

1. Go to Bus Monitor tab, use simulated traffic or live connection
2. Load a DBC file, wait for signals to appear in Signal panel
3. Right-click a signal → "Add to Instrument Panel"
4. Switch to Instruments tab (bottom section) → widget appears

#### INS-2: Widget types

1. Add a signal to instrument panel
2. Right-click the widget → "Change Type"
3. Cycles through: Numeric → Bar → Gauge → Boolean → Enum
4. Each type renders correctly with live updating values

#### INS-3: Remove widget

1. Right-click a widget → "Remove"
2. Widget disappears from panel

#### INS-4: Multiple widgets

1. Add 3-4 different signals to instrument panel
2. Widgets arrange in a WrapPanel (flow layout)
3. All update independently with live values

#### INS-5: Instrument layout in project

1. Set up several instrument widgets
2. File > Save Project
3. Close and reopen project
4. Instrument panel restored with same widgets and types

### Instrument Panel Test Checklist

| # | Test | Hardware | Status |
|---|------|----------|--------|
| — | Unit tests (7 instrument-specific) | None | |
| INS-1 | Add signal to instrument panel | None | |
| INS-2 | Widget types cycle | None | |
| INS-3 | Remove widget | None | |
| INS-4 | Multiple widgets | None | |
| INS-5 | Layout in project save/load | None | |
```

- [ ] **Step 4: Verify build and all tests**

- [ ] **Step 5: Commit**

```
feat: wire instrument layouts into project system and update test guide
```

---

## Summary

| Task | Component | Tests | Depends On |
|------|-----------|-------|------------|
| 1 | InstrumentWidget model + ViewModel | 7 | — |
| 2 | Widget UserControls (5 types) | 0 | 1 |
| 3 | InstrumentPanel view + Bus Monitor wiring | 0 | 1, 2 |
| 4 | Project system integration + test guide | 0 | 1, 3 |

**Total: 4 tasks, 7 new tests**
