using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CanLinConfig.Models;

namespace CanLinConfig.Views.Widgets;

public partial class GaugeWidget : UserControl
{
    private const double Cx = 70;
    private const double Cy = 70;
    private const double ArcRadius = 60;
    private const double NeedleRadius = 50;
    private const double StartAngle = -200;
    private const double SweepAngle = 220;

    public GaugeWidget()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DrawArc();
        UpdateNeedle();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is InstrumentWidget oldWidget)
            oldWidget.PropertyChanged -= OnWidgetPropertyChanged;

        if (e.NewValue is InstrumentWidget newWidget)
        {
            newWidget.PropertyChanged += OnWidgetPropertyChanged;
            DrawArc();
            UpdateNeedle();
        }
    }

    private void OnWidgetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstrumentWidget.NormalizedValue))
            Dispatcher.BeginInvoke(UpdateNeedle);
    }

    private static Point PointOnArc(double cx, double cy, double r, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }

    private void DrawArc()
    {
        var start = PointOnArc(Cx, Cy, ArcRadius, StartAngle);
        var end = PointOnArc(Cx, Cy, ArcRadius, StartAngle + SweepAngle);
        var size = new Size(ArcRadius, ArcRadius);

        var figure = new PathFigure { StartPoint = start };
        figure.Segments.Add(new ArcSegment(end, size, 0,
            SweepAngle > 180, SweepDirection.Clockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        ArcPath.Data = geometry;
    }

    private void UpdateNeedle()
    {
        double normalized = 0;
        if (DataContext is InstrumentWidget widget)
            normalized = widget.NormalizedValue;

        double angle = StartAngle + normalized * SweepAngle;
        var tip = PointOnArc(Cx, Cy, NeedleRadius, angle);

        Needle.X1 = Cx;
        Needle.Y1 = Cy;
        Needle.X2 = tip.X;
        Needle.Y2 = tip.Y;

        Canvas.SetLeft(CenterDot, Cx - 4);
        Canvas.SetTop(CenterDot, Cy - 4);
    }
}
