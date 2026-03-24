# Instrument Panel Enhancements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade instrument panel widgets with a type-selector submenu, a configurable BitPanel widget, and free-position drag with snap-to-edge alignment.

**Architecture:** Three independent features layered onto the existing InstrumentPanel. Task 1 extends the model with new fields. Task 2 replaces the cycle command with a submenu. Task 3 adds the BitPanel widget and config dialog. Task 4 converts the WrapPanel to a Canvas with drag+snap. Each task builds on the previous and produces a testable increment.

**Tech Stack:** .NET 8, WPF, CommunityToolkit.Mvvm, MahApps.Metro dark theme

**Spec:** `docs/superpowers/specs/2026-03-24-instrument-panel-enhancements-design.md`

---

### Task 1: Extend Model — BitPanel enum, X/Y position, BitCount, BitLabels

**Files:**
- Modify: `software/CanLinConfig/Models/InstrumentWidget.cs`
- Modify: `software/CanLinConfig/ViewModels/InstrumentPanelViewModel.cs`
- Modify: `software/CanLinConfig.Tests/InstrumentPanelViewModelTests.cs`

- [ ] **Step 1: Add BitPanel to WidgetType enum and new fields to InstrumentWidget**

In `Models/InstrumentWidget.cs`:

```csharp
public enum WidgetType { Numeric, Bar, Gauge, Boolean, Enum, BitPanel }
```

Add new fields to `InstrumentWidget`:

```csharp
public int BitCount { get; set; } = 8;
public List<string> BitLabels { get; set; } = [];

[ObservableProperty] private double _x;
[ObservableProperty] private double _y;
```

Add new JSON fields to `WidgetLayout`:

```csharp
[JsonPropertyName("bit_count")] public int BitCount { get; set; } = 8;
[JsonPropertyName("bit_labels")] public List<string> BitLabels { get; set; } = [];
[JsonPropertyName("x")] public double X { get; set; }
[JsonPropertyName("y")] public double Y { get; set; }
```

- [ ] **Step 2: Update ToLayouts and FromLayouts to serialize new fields**

In `ViewModels/InstrumentPanelViewModel.cs`, update `ToLayouts`:

```csharp
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
```

Update `FromLayouts` — replace the `AddWidget` call body to also set the new fields:

```csharp
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
```

- [ ] **Step 3: Write tests for new model fields and round-trip**

In `CanLinConfig.Tests/InstrumentPanelViewModelTests.cs`, add:

```csharp
[Fact]
public void ToLayouts_includes_position_and_bitpanel_fields()
{
    var vm = new InstrumentPanelViewModel();
    vm.AddWidget("0:256:Status", "Status", "", WidgetType.BitPanel, 0, 255);
    vm.Widgets[0].BitCount = 4;
    vm.Widgets[0].BitLabels = ["Error", "Active", "Ready", "Run"];
    vm.Widgets[0].X = 100;
    vm.Widgets[0].Y = 50;

    var layouts = vm.ToLayouts();
    Assert.Equal(4, layouts[0].BitCount);
    Assert.Equal(["Error", "Active", "Ready", "Run"], layouts[0].BitLabels);
    Assert.Equal(100, layouts[0].X);
    Assert.Equal(50, layouts[0].Y);
}

[Fact]
public void FromLayouts_restores_position_and_bitpanel_fields()
{
    var layout = new WidgetLayout
    {
        SignalKey = "0:256:Status", SignalName = "Status", Unit = "",
        Type = "BitPanel", BitCount = 3, BitLabels = ["A", "B", "C"],
        X = 200, Y = 75
    };
    var vm = new InstrumentPanelViewModel();
    vm.FromLayouts([layout]);

    Assert.Equal(WidgetType.BitPanel, vm.Widgets[0].Type);
    Assert.Equal(3, vm.Widgets[0].BitCount);
    Assert.Equal(["A", "B", "C"], vm.Widgets[0].BitLabels);
    Assert.Equal(200, vm.Widgets[0].X);
    Assert.Equal(75, vm.Widgets[0].Y);
}

[Fact]
public void FromLayouts_generates_default_bit_labels_when_empty()
{
    var layout = new WidgetLayout
    {
        SignalKey = "0:256:Flags", SignalName = "Flags", Unit = "",
        Type = "BitPanel", BitCount = 4, BitLabels = []
    };
    var vm = new InstrumentPanelViewModel();
    vm.FromLayouts([layout]);
    Assert.Equal(["Bit 0", "Bit 1", "Bit 2", "Bit 3"], vm.Widgets[0].BitLabels);
}
```

- [ ] **Step 4: Run tests**

Run: `cd software && dotnet test CanLinConfig.Tests -v minimal`
Expected: All tests pass (existing + 3 new)

- [ ] **Step 5: Commit**

```
feat: extend instrument widget model with BitPanel, X/Y, BitCount, BitLabels
```

---

### Task 2: Widget Type Selector Submenu

**Files:**
- Create: `software/CanLinConfig/Helpers/EnumMatchConverter.cs`
- Modify: `software/CanLinConfig/ViewModels/InstrumentPanelViewModel.cs`
- Modify: `software/CanLinConfig/Views/InstrumentPanel.xaml`
- Modify: `software/CanLinConfig/Views/InstrumentPanel.xaml.cs`

- [ ] **Step 1: Create EnumMatchConverter**

Create `Helpers/EnumMatchConverter.cs`:

```csharp
using System.Globalization;
using System.Windows.Data;

namespace CanLinConfig.Helpers;

public class EnumMatchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return targetType == typeof(Visibility) ? Visibility.Collapsed : (object)false;
        bool match = value.ToString() == parameter.ToString();
        if (targetType == typeof(Visibility))
            return match ? Visibility.Visible : Visibility.Collapsed;
        return match;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 2: Replace CycleWidgetType with ChangeWidgetType in ViewModel**

In `ViewModels/InstrumentPanelViewModel.cs`, replace the `CycleWidgetType` command with:

```csharp
// Note: Remove the old [RelayCommand] CycleWidgetType — replaced by SetWidgetType called from code-behind.

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
```

Also add the event for BitPanel config dialog:

```csharp
public event Action<InstrumentWidget>? RequestBitPanelConfig;

public void RaiseBitPanelConfig(InstrumentWidget widget) => RequestBitPanelConfig?.Invoke(widget);
```

- [ ] **Step 3: Add BitPanel to WidgetTemplateSelector**

In `Views/InstrumentPanel.xaml.cs`, add to `WidgetTemplateSelector`:

```csharp
public DataTemplate? BitPanelTemplate { get; set; }
```

And add the case in `SelectTemplate`:

```csharp
WidgetType.BitPanel => BitPanelTemplate,
```

- [ ] **Step 4: Update InstrumentPanel.xaml with submenu**

Replace the entire XAML content. Key changes:
- Add `EnumMatchConverter` and `BitPanelTpl` resources
- Replace single "Change Type" MenuItem with submenu containing one item per type
- Each submenu item: `Header="Numeric"`, `IsCheckable="True"`, `IsChecked` bound via converter, click handler in code-behind
- Add "Edit BitPanel..." item (visible only when Type is BitPanel)
- Add `BitPanelTemplate` to `WidgetSelector`

The context menu submenu items use code-behind click handlers (simplest WPF approach):

```xml
<MenuItem Header="Change Type">
    <MenuItem Header="Numeric" Tag="Numeric" Click="OnChangeType"
              IsCheckable="True" IsChecked="{Binding Type, Converter={StaticResource EnumMatch}, ConverterParameter=Numeric, Mode=OneWay}"/>
    <MenuItem Header="Bar" Tag="Bar" Click="OnChangeType"
              IsCheckable="True" IsChecked="{Binding Type, Converter={StaticResource EnumMatch}, ConverterParameter=Bar, Mode=OneWay}"/>
    <MenuItem Header="Gauge" Tag="Gauge" Click="OnChangeType"
              IsCheckable="True" IsChecked="{Binding Type, Converter={StaticResource EnumMatch}, ConverterParameter=Gauge, Mode=OneWay}"/>
    <MenuItem Header="Boolean" Tag="Boolean" Click="OnChangeType"
              IsCheckable="True" IsChecked="{Binding Type, Converter={StaticResource EnumMatch}, ConverterParameter=Boolean, Mode=OneWay}"/>
    <MenuItem Header="BitPanel" Tag="BitPanel" Click="OnChangeType"
              IsCheckable="True" IsChecked="{Binding Type, Converter={StaticResource EnumMatch}, ConverterParameter=BitPanel, Mode=OneWay}"/>
    <MenuItem Header="Enum" Tag="Enum" Click="OnChangeType"
              IsCheckable="True" IsChecked="{Binding Type, Converter={StaticResource EnumMatch}, ConverterParameter=Enum, Mode=OneWay}"/>
</MenuItem>
<MenuItem Header="Edit BitPanel..." Click="OnEditBitPanel"
          Visibility="{Binding Type, Converter={StaticResource EnumMatch}, ConverterParameter=BitPanel}"/>
```

Note: "Edit BitPanel..." visibility needs a `BoolToVisibilityConverter` or use the `EnumMatchConverter` with a fallback. Simplest: use code-behind to toggle visibility when the context menu opens.

- [ ] **Step 5: Add click handlers in InstrumentPanel.xaml.cs**

```csharp
private void OnChangeType(object sender, RoutedEventArgs e)
{
    if (sender is not MenuItem mi || mi.DataContext is not InstrumentWidget widget) return;
    if (!Enum.TryParse<WidgetType>(mi.Tag?.ToString(), out var newType)) return;

    if (DataContext is InstrumentPanelViewModel vm)
    {
        vm.SetWidgetType(widget, newType);
        if (newType == WidgetType.BitPanel && widget.BitLabels.Count == 0)
            ShowBitPanelConfig(widget);
    }
}

private void OnEditBitPanel(object sender, RoutedEventArgs e)
{
    if (sender is not MenuItem mi || mi.DataContext is not InstrumentWidget widget) return;
    ShowBitPanelConfig(widget);
}

private void ShowBitPanelConfig(InstrumentWidget widget)
{
    // Placeholder — implemented in Task 3
}
```

- [ ] **Step 6: Build and verify submenu works**

Run: `cd software && dotnet build CanLinConfig.sln`
Expected: 0 errors. Launch app, add a signal to instrument panel, right-click → "Change Type" submenu shows all 6 types with checkmark on current.

- [ ] **Step 7: Commit**

```
feat: widget type selector submenu with per-type items and checkmarks
```

---

### Task 3: BitPanel Widget and Config Dialog

**Files:**
- Create: `software/CanLinConfig/Views/Widgets/BitPanelWidget.xaml`
- Create: `software/CanLinConfig/Views/Widgets/BitPanelWidget.xaml.cs`
- Create: `software/CanLinConfig/Views/Widgets/BitPanelConfigDialog.xaml`
- Create: `software/CanLinConfig/Views/Widgets/BitPanelConfigDialog.xaml.cs`
- Modify: `software/CanLinConfig/Views/InstrumentPanel.xaml` (add BitPanelTpl)
- Modify: `software/CanLinConfig/Views/InstrumentPanel.xaml.cs` (ShowBitPanelConfig)
- Modify: `software/CanLinConfig.Tests/InstrumentPanelViewModelTests.cs`

- [ ] **Step 1: Create BitPanelWidget.xaml**

```xml
<UserControl x:Class="CanLinConfig.Views.Widgets.BitPanelWidget"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Width="200" MinHeight="60">
    <Border BorderBrush="#555" BorderThickness="1" CornerRadius="4" Padding="8" Background="#1E1E1E">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
            </Grid.RowDefinitions>
            <TextBlock Grid.Row="0" Text="{Binding SignalName}" FontSize="11" Foreground="#AAA" Margin="0,0,0,4"/>
            <StackPanel Grid.Row="1" x:Name="BitRows"/>
        </Grid>
    </Border>
</UserControl>
```

- [ ] **Step 2: Create BitPanelWidget.xaml.cs**

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CanLinConfig.Models;

namespace CanLinConfig.Views.Widgets;

public partial class BitPanelWidget : UserControl
{
    private readonly List<Ellipse> _lamps = [];
    private readonly List<TextBlock> _labels = [];

    public BitPanelWidget()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is InstrumentWidget oldW)
            oldW.PropertyChanged -= OnWidgetPropertyChanged;

        if (e.NewValue is InstrumentWidget newW)
        {
            newW.PropertyChanged += OnWidgetPropertyChanged;
            BuildRows(newW);
            UpdateLamps(newW);
        }
    }

    private void BuildRows(InstrumentWidget widget)
    {
        _lamps.Clear();
        _labels.Clear();
        var panel = new StackPanel();

        for (int i = 0; i < widget.BitCount; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            var lamp = new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)), Margin = new Thickness(0, 0, 6, 0) };
            var label = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), VerticalAlignment = VerticalAlignment.Center };
            label.Text = i < widget.BitLabels.Count ? widget.BitLabels[i] : $"Bit {i}";

            row.Children.Add(lamp);
            row.Children.Add(label);
            panel.Children.Add(row);
            _lamps.Add(lamp);
            _labels.Add(label);
        }

        BitRows.Children.Clear();
        foreach (UIElement child in panel.Children.Cast<UIElement>().ToList())
        {
            panel.Children.Remove(child);
            BitRows.Children.Add(child);
        }
    }

    private void OnWidgetPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstrumentWidget.Value) && sender is InstrumentWidget w)
            Dispatcher.BeginInvoke(() => UpdateLamps(w));
    }

    private static readonly SolidColorBrush BrushOn = new(Color.FromRgb(0x4C, 0xD9, 0x64));
    private static readonly SolidColorBrush BrushOff = new(Color.FromRgb(0x44, 0x44, 0x44));

    private void UpdateLamps(InstrumentWidget widget)
    {
        uint raw = (uint)(long)widget.Value;
        for (int i = 0; i < _lamps.Count; i++)
            _lamps[i].Fill = ((raw >> i) & 1) != 0 ? BrushOn : BrushOff;
    }
}
```

- [ ] **Step 3: Create BitPanelConfigDialog.xaml**

```xml
<Window x:Class="CanLinConfig.Views.Widgets.BitPanelConfigDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Configure BitPanel" Width="320" SizeToContent="Height"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize"
        Background="#1E1E1E">
    <StackPanel Margin="16">
        <TextBlock Text="Number of bits:" Foreground="#CCC" Margin="0,0,0,4"/>
        <StackPanel Orientation="Horizontal" Margin="0,0,0,12">
            <TextBox x:Name="BitCountBox" Width="50" Text="8" VerticalContentAlignment="Center"/>
            <TextBlock Text="(1–8)" Foreground="#888" VerticalAlignment="Center" Margin="8,0,0,0"/>
        </StackPanel>
        <TextBlock Text="Bit labels (bit 0 at top):" Foreground="#CCC" Margin="0,0,0,4"/>
        <StackPanel x:Name="LabelPanel"/>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button Content="OK" Width="75" Margin="0,0,8,0" Click="OnOk" IsDefault="True"/>
            <Button Content="Cancel" Width="75" Click="OnCancel" IsCancel="True"/>
        </StackPanel>
    </StackPanel>
</Window>
```

- [ ] **Step 4: Create BitPanelConfigDialog.xaml.cs**

```csharp
using System.Windows;
using System.Windows.Controls;

namespace CanLinConfig.Views.Widgets;

public partial class BitPanelConfigDialog : Window
{
    private readonly TextBox[] _labelBoxes = new TextBox[8];

    public int ResultBitCount { get; private set; }
    public List<string> ResultLabels { get; private set; } = [];

    public BitPanelConfigDialog(int bitCount, List<string> labels)
    {
        InitializeComponent();
        BitCountBox.Text = bitCount.ToString();
        BitCountBox.TextChanged += (_, _) => RebuildLabels();
        ResultBitCount = bitCount;
        ResultLabels = new List<string>(labels);
        RebuildLabels();
    }

    private void RebuildLabels()
    {
        if (!int.TryParse(BitCountBox.Text, out int count)) return;
        count = Math.Clamp(count, 1, 8);
        LabelPanel.Children.Clear();

        for (int i = 0; i < count; i++)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(new TextBlock { Text = $"Bit {i}:", Width = 45, Foreground = System.Windows.Media.Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center });
            var tb = new TextBox { Width = 200, Text = i < ResultLabels.Count ? ResultLabels[i] : $"Bit {i}" };
            _labelBoxes[i] = tb;
            sp.Children.Add(tb);
            LabelPanel.Children.Add(sp);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(BitCountBox.Text, out int count)) return;
        count = Math.Clamp(count, 1, 8);
        ResultBitCount = count;
        ResultLabels = new List<string>();
        for (int i = 0; i < count; i++)
            ResultLabels.Add(_labelBoxes[i]?.Text ?? $"Bit {i}");
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
```

- [ ] **Step 5: Wire up ShowBitPanelConfig in InstrumentPanel.xaml.cs**

Replace the placeholder `ShowBitPanelConfig`:

```csharp
private void ShowBitPanelConfig(InstrumentWidget widget)
{
    var labels = widget.BitLabels.Count > 0
        ? new List<string>(widget.BitLabels)
        : Enumerable.Range(0, widget.BitCount).Select(i => $"Bit {i}").ToList();

    var dlg = new Widgets.BitPanelConfigDialog(widget.BitCount, labels)
    {
        Owner = Window.GetWindow(this)
    };

    if (dlg.ShowDialog() == true)
    {
        widget.BitCount = dlg.ResultBitCount;
        widget.BitLabels = dlg.ResultLabels;
        // Force re-render
        if (DataContext is InstrumentPanelViewModel vm)
            vm.ForceTemplateRefresh(widget);
    }
}
```

- [ ] **Step 6: Add BitPanelTpl to InstrumentPanel.xaml resources**

Add to the Resources section:

```xml
<DataTemplate x:Key="BitPanelTpl"><widgets:BitPanelWidget/></DataTemplate>
```

And add to the WidgetSelector:

```xml
BitPanelTemplate="{StaticResource BitPanelTpl}"
```

- [ ] **Step 7: Add unit test for BitPanel value decomposition**

In `InstrumentPanelViewModelTests.cs`:

```csharp
[Fact]
public void BitPanel_value_decomposes_to_bits()
{
    var widget = new InstrumentWidget
    {
        Type = WidgetType.BitPanel, BitCount = 4,
        BitLabels = ["A", "B", "C", "D"]
    };
    widget.UpdateValue(0b1010); // bits 1 and 3 set
    uint raw = (uint)(long)widget.Value;
    Assert.Equal(0u, (raw >> 0) & 1); // A = off
    Assert.Equal(1u, (raw >> 1) & 1); // B = on
    Assert.Equal(0u, (raw >> 2) & 1); // C = off
    Assert.Equal(1u, (raw >> 3) & 1); // D = on
}
```

- [ ] **Step 8: Build and test**

Run: `cd software && dotnet test CanLinConfig.Tests -v minimal`
Expected: All tests pass. Launch app, add signal, change to BitPanel, configure 4 bits, verify lamps update with signal value.

- [ ] **Step 9: Commit**

```
feat: BitPanel widget with configurable bit labels and config dialog
```

---

### Task 4: Free-Position Drag with Snap

**Files:**
- Modify: `software/CanLinConfig/Views/InstrumentPanel.xaml`
- Modify: `software/CanLinConfig/Views/InstrumentPanel.xaml.cs`
- Modify: `software/CanLinConfig/ViewModels/InstrumentPanelViewModel.cs`
- Modify: `software/CanLinConfig.Tests/InstrumentPanelViewModelTests.cs`

- [ ] **Step 1: Update InstrumentPanel.xaml — replace WrapPanel with Canvas**

Change the `ItemsControl.ItemsPanel` from `WrapPanel` to `Canvas`. Add a guideline overlay Canvas on top (same grid cell, `IsHitTestVisible="False"`) for snap lines — cannot add children directly to an ItemsControl-managed Canvas.

```xml
<Grid Grid.Row="1">
    <ItemsControl x:Name="WidgetItems" ItemsSource="{Binding Widgets}"
                  ItemTemplateSelector="{StaticResource WidgetSelector}"
                  ClipToBounds="True">
        <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate><Canvas/></ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemContainerStyle>
            <Style TargetType="ContentPresenter">
                <Setter Property="Canvas.Left" Value="{Binding X, Mode=TwoWay}"/>
                <Setter Property="Canvas.Top" Value="{Binding Y, Mode=TwoWay}"/>
                <!-- ContextMenu here (same as before) -->
            </Style>
        </ItemsControl.ItemContainerStyle>
    </ItemsControl>
    <Canvas x:Name="GuidelineCanvas" IsHitTestVisible="False"/>
</Grid>
```

**Key WPF details:**
- Canvas inside ItemsControl has zero ActualWidth/ActualHeight — use `WidgetItems.ActualWidth`/`ActualHeight` for snap-to-edge calculations
- Guideline Lines go on `GuidelineCanvas` (overlay), NOT on the items Canvas (which would throw InvalidOperationException)
- `ClipToBounds="True"` prevents widgets from rendering outside the panel

- [ ] **Step 2: Add auto-placement logic to InstrumentPanelViewModel**

Update `AddWidget` to set initial position:

```csharp
public void AddWidget(string signalKey, string signalName, string unit,
    WidgetType type = WidgetType.Numeric, double rangeMin = 0, double rangeMax = 100)
{
    if (_widgetMap.ContainsKey(signalKey)) return;
    var widget = new InstrumentWidget
    {
        SignalKey = signalKey, SignalName = signalName, Unit = unit,
        Type = type, RangeMin = rangeMin, RangeMax = rangeMax,
    };

    // Auto-place below the lowest widget
    widget.X = 8;
    widget.Y = Widgets.Count == 0 ? 8 : Widgets.Max(w => w.Y + 120) + 8;

    _widgetMap[signalKey] = widget;
    Widgets.Add(widget);
}
```

Add backward-compat auto-layout after `FromLayouts`:

```csharp
public void AutoLayoutIfStacked()
{
    if (Widgets.Count <= 1) return;
    bool allZero = Widgets.All(w => w.X == 0 && w.Y == 0);
    if (!allZero) return;

    for (int i = 0; i < Widgets.Count; i++)
    {
        Widgets[i].X = 8;
        Widgets[i].Y = i * 120;
    }
}
```

Call `AutoLayoutIfStacked()` from `FromLayouts` at the end.

- [ ] **Step 3: Implement drag + snap in InstrumentPanel.xaml.cs**

Add fields and methods to the `InstrumentPanel` code-behind:

```csharp
// Drag state
private bool _isDragging;
private ContentPresenter? _dragTarget;
private InstrumentWidget? _dragWidget;
private Point _dragOffset;
private double _dragStartX, _dragStartY;
private readonly List<Line> _guidelines = [];

// Called on PreviewMouseLeftButtonDown on ItemsControl
private void OnWidgetMouseDown(object sender, MouseButtonEventArgs e)
{
    var cp = FindContentPresenter(e.OriginalSource as DependencyObject);
    if (cp?.DataContext is not InstrumentWidget widget) return;

    _isDragging = true;
    _dragTarget = cp;
    _dragWidget = widget;
    _dragOffset = e.GetPosition(cp);
    _dragStartX = widget.X;
    _dragStartY = widget.Y;
    cp.CaptureMouse();
    Canvas.SetZIndex(cp, 1000);
    e.Handled = true;
}

private void OnWidgetMouseMove(object sender, MouseEventArgs e)
{
    if (!_isDragging || _dragWidget == null || _dragTarget == null) return;
    var ic = FindItemsControl();
    if (ic == null) return;

    var pos = e.GetPosition(ic);
    double newX = pos.X - _dragOffset.X;
    double newY = pos.Y - _dragOffset.Y;

    // Compute snap
    (newX, newY) = ComputeSnap(newX, newY, _dragTarget.ActualWidth, _dragTarget.ActualHeight);

    _dragWidget.X = Math.Max(0, newX);
    _dragWidget.Y = Math.Max(0, newY);
}

private void OnWidgetMouseUp(object sender, MouseButtonEventArgs e)
{
    if (!_isDragging || _dragTarget == null) return;
    _dragTarget.ReleaseMouseCapture();
    Canvas.SetZIndex(_dragTarget, 0);
    ClearGuidelines();
    _isDragging = false;
    _dragTarget = null;
    _dragWidget = null;
}

private void OnKeyDown(object sender, KeyEventArgs e)
{
    if (e.Key == Key.Escape && _isDragging && _dragWidget != null && _dragTarget != null)
    {
        _dragWidget.X = _dragStartX;
        _dragWidget.Y = _dragStartY;
        _dragTarget.ReleaseMouseCapture();
        Canvas.SetZIndex(_dragTarget, 0);
        ClearGuidelines();
        _isDragging = false;
        _dragTarget = null;
        _dragWidget = null;
    }
}
```

- [ ] **Step 4: Implement snap logic**

```csharp
private const double SnapThreshold = 8.0;

private (double x, double y) ComputeSnap(double x, double y, double w, double h)
{
    ClearGuidelines();
    var ic = FindItemsControl();
    if (ic == null) return (x, y);

    double bestDx = double.MaxValue, snapX = x;
    double bestDy = double.MaxValue, snapY = y;

    // Snap to panel edges (use ItemsControl size — Canvas inside has zero size)
    TrySnapX(x, 0, ref bestDx, ref snapX);
    TrySnapX(x + w, ic.ActualWidth, ref bestDx, ref snapX, -w);
    TrySnapY(y, 0, ref bestDy, ref snapY);
    TrySnapY(y + h, ic.ActualHeight, ref bestDy, ref snapY, -h);

    // Snap to other widgets
    {
        foreach (var item in ic.Items)
        {
            if (item == _dragWidget) continue;
            var cp = ic.ItemContainerGenerator.ContainerFromItem(item) as ContentPresenter;
            if (cp == null) continue;
            double ox = Canvas.GetLeft(cp), oy = Canvas.GetTop(cp);
            double ow = cp.ActualWidth, oh = cp.ActualHeight;
            if (double.IsNaN(ox)) ox = 0;
            if (double.IsNaN(oy)) oy = 0;

            // Left edge to right edge, right to left, etc.
            TrySnapX(x, ox + ow, ref bestDx, ref snapX);      // my left → other right
            TrySnapX(x + w, ox, ref bestDx, ref snapX, -w);    // my right → other left
            TrySnapX(x, ox, ref bestDx, ref snapX);             // my left → other left
            TrySnapX(x + w, ox + ow, ref bestDx, ref snapX, -w); // my right → other right

            TrySnapY(y, oy + oh, ref bestDy, ref snapY);
            TrySnapY(y + h, oy, ref bestDy, ref snapY, -h);
            TrySnapY(y, oy, ref bestDy, ref snapY);
            TrySnapY(y + h, oy + oh, ref bestDy, ref snapY, -h);
        }
    }

    // Draw guidelines at the snap edge (not the widget position)
    if (bestDx < SnapThreshold) AddGuideline(true, snapX + (snapX != x ? 0 : 0));
    if (bestDy < SnapThreshold) AddGuideline(false, snapY + (snapY != y ? 0 : 0));

    return (bestDx < SnapThreshold ? snapX : x,
            bestDy < SnapThreshold ? snapY : y);
}

private static void TrySnapX(double edge, double target, ref double bestDist, ref double snapResult, double offset = 0)
{
    double dist = Math.Abs(edge - target);
    if (dist < bestDist) { bestDist = dist; snapResult = target + offset; }
}

private static void TrySnapY(double edge, double target, ref double bestDist, ref double snapResult, double offset = 0)
{
    double dist = Math.Abs(edge - target);
    if (dist < bestDist) { bestDist = dist; snapResult = target + offset; }
}

private void AddGuideline(bool vertical, double pos)
{
    var ic = FindItemsControl();
    if (ic == null) return;
    var line = new Line
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7)),
        StrokeThickness = 1, SnapsToDevicePixels = true
    };
    if (vertical) { line.X1 = line.X2 = pos; line.Y1 = 0; line.Y2 = ic.ActualHeight; }
    else          { line.Y1 = line.Y2 = pos; line.X1 = 0; line.X2 = ic.ActualWidth; }
    GuidelineCanvas.Children.Add(line);
    _guidelines.Add(line);
}

private void ClearGuidelines()
{
    foreach (var line in _guidelines) GuidelineCanvas.Children.Remove(line);
    _guidelines.Clear();
}
```

- [ ] **Step 5: Wire up events in XAML / constructor**

In `InstrumentPanel` constructor, after `InitializeComponent()`:

```csharp
PreviewMouseLeftButtonDown += OnWidgetMouseDown;
PreviewMouseMove += OnWidgetMouseMove;
PreviewMouseLeftButtonUp += OnWidgetMouseUp;
KeyDown += OnKeyDown;
ContextMenuOpening += (s, e) => { if (_isDragging) e.Handled = true; };
Focusable = true;
```

Add helper methods:

```csharp
private static ContentPresenter? FindContentPresenter(DependencyObject? d)
{
    while (d != null)
    {
        if (d is ContentPresenter cp && cp.DataContext is InstrumentWidget) return cp;
        d = VisualTreeHelper.GetParent(d);
    }
    return null;
}

private ItemsControl? FindItemsControl() => this.FindName("WidgetItems") as ItemsControl;
```

Give the ItemsControl a name in XAML: `x:Name="WidgetItems"`

- [ ] **Step 6: Add test for auto-layout backward compatibility**

In `InstrumentPanelViewModelTests.cs`:

```csharp
[Fact]
public void AutoLayoutIfStacked_spreads_widgets_vertically()
{
    var vm = new InstrumentPanelViewModel();
    var layouts = new List<WidgetLayout>
    {
        new() { SignalKey = "0:256:A", SignalName = "A", Unit = "", Type = "Numeric", X = 0, Y = 0 },
        new() { SignalKey = "0:256:B", SignalName = "B", Unit = "", Type = "Bar", X = 0, Y = 0 },
        new() { SignalKey = "0:256:C", SignalName = "C", Unit = "", Type = "Gauge", X = 0, Y = 0 },
    };
    vm.FromLayouts(layouts);

    // All at 0,0 → auto-layout should spread them
    Assert.Equal(8, vm.Widgets[0].X);
    Assert.Equal(0, vm.Widgets[0].Y);
    Assert.Equal(8, vm.Widgets[1].X);
    Assert.Equal(120, vm.Widgets[1].Y);
    Assert.Equal(8, vm.Widgets[2].X);
    Assert.Equal(240, vm.Widgets[2].Y);
}

[Fact]
public void AutoLayoutIfStacked_does_not_move_positioned_widgets()
{
    var vm = new InstrumentPanelViewModel();
    var layouts = new List<WidgetLayout>
    {
        new() { SignalKey = "0:256:A", SignalName = "A", Unit = "", Type = "Numeric", X = 50, Y = 30 },
        new() { SignalKey = "0:256:B", SignalName = "B", Unit = "", Type = "Bar", X = 200, Y = 30 },
    };
    vm.FromLayouts(layouts);

    Assert.Equal(50, vm.Widgets[0].X);
    Assert.Equal(30, vm.Widgets[0].Y);
    Assert.Equal(200, vm.Widgets[1].X);
    Assert.Equal(30, vm.Widgets[1].Y);
}
```

- [ ] **Step 7: Run all tests**

Run: `cd software && dotnet test CanLinConfig.Tests -v minimal`
Expected: All tests pass.

- [ ] **Step 8: Manual verification**

Launch app, connect to board, add 3-4 signals to instrument panel:
1. Widgets auto-placed vertically
2. Drag a widget — snap guidelines appear near edges
3. Snap to other widget edges works
4. Escape cancels drag
5. Save project, close, reopen — positions preserved
6. Right-click → Change Type submenu works, BitPanel configurable

- [ ] **Step 9: Commit**

```
feat: free-position drag with snap-to-edge alignment on instrument panel
```
