using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CanLinConfig.Models;

namespace CanLinConfig.Views.Widgets;

public partial class BooleanWidget : UserControl
{
    private static readonly SolidColorBrush OnBrush = new(Color.FromRgb(0x00, 0xCC, 0x00));
    private static readonly SolidColorBrush OffBrush = new(Color.FromRgb(0x44, 0x44, 0x44));

    public BooleanWidget()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is InstrumentWidget oldWidget)
            oldWidget.PropertyChanged -= OnWidgetPropertyChanged;

        if (e.NewValue is InstrumentWidget newWidget)
        {
            newWidget.PropertyChanged += OnWidgetPropertyChanged;
            UpdateLamp(newWidget.Value);
        }
    }

    private void OnWidgetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstrumentWidget.Value) && sender is InstrumentWidget w)
            Dispatcher.BeginInvoke(() => UpdateLamp(w.Value));
    }

    private void UpdateLamp(double value)
    {
        Lamp.Fill = value != 0 ? OnBrush : OffBrush;
    }
}
