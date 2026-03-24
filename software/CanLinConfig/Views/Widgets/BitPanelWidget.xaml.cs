using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CanLinConfig.Models;

namespace CanLinConfig.Views.Widgets;

public partial class BitPanelWidget : UserControl
{
    private readonly List<Ellipse> _lamps = [];

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
        BitRows.Children.Clear();

        for (int i = 0; i < widget.BitCount; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            var lamp = new Ellipse
            {
                Width = 14, Height = 14,
                Fill = BrushOff,
                Margin = new Thickness(0, 0, 6, 0)
            };
            var label = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                VerticalAlignment = VerticalAlignment.Center,
                Text = i < widget.BitLabels.Count ? widget.BitLabels[i] : $"Bit {i}"
            };

            row.Children.Add(lamp);
            row.Children.Add(label);
            BitRows.Children.Add(row);
            _lamps.Add(lamp);
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
        uint raw = unchecked((uint)(long)widget.Value);
        for (int i = 0; i < _lamps.Count; i++)
            _lamps[i].Fill = ((raw >> i) & 1) != 0 ? BrushOn : BrushOff;
    }
}
