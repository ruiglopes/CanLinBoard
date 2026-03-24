using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CanLinConfig.Models;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Views;

public partial class InstrumentPanel : UserControl
{
    private bool _isDragging;
    private ContentPresenter? _dragTarget;
    private InstrumentWidget? _dragWidget;
    private Point _dragOffset;
    private double _dragStartX, _dragStartY;
    private readonly List<Line> _guidelines = [];

    public InstrumentPanel()
    {
        InitializeComponent();

        PreviewMouseLeftButtonDown += OnWidgetMouseDown;
        PreviewMouseMove += OnWidgetMouseMove;
        PreviewMouseLeftButtonUp += OnWidgetMouseUp;
        KeyDown += OnKeyDown;
        ContextMenuOpening += (s, e) => { if (_isDragging) e.Handled = true; };
        DataContextChanged += (s, e) =>
        {
            if (e.OldValue is InstrumentPanelViewModel oldVm)
                oldVm.RequestBitPanelConfig -= OnRequestBitPanelConfig;
            if (e.NewValue is InstrumentPanelViewModel newVm)
                newVm.RequestBitPanelConfig += OnRequestBitPanelConfig;
        };
        Focusable = true;
    }

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

    // ---- Snap logic ----

    private const double SnapThreshold = 8.0;

    private (double x, double y) ComputeSnap(double x, double y, double w, double h)
    {
        ClearGuidelines();
        var ic = FindItemsControl();
        if (ic == null) return (x, y);

        double bestDx = double.MaxValue, snapX = x;
        double bestDy = double.MaxValue, snapY = y;

        // Snap to panel edges
        TrySnapX(x,     0,              ref bestDx, ref snapX);
        TrySnapX(x + w, ic.ActualWidth, ref bestDx, ref snapX, -w);
        TrySnapY(y,     0,               ref bestDy, ref snapY);
        TrySnapY(y + h, ic.ActualHeight, ref bestDy, ref snapY, -h);

        // Snap to other widgets
        foreach (var item in ic.Items)
        {
            if (item == _dragWidget) continue;
            var cp = ic.ItemContainerGenerator.ContainerFromItem(item) as ContentPresenter;
            if (cp == null) continue;
            double ox = Canvas.GetLeft(cp), oy = Canvas.GetTop(cp);
            double ow = cp.ActualWidth,     oh = cp.ActualHeight;
            if (double.IsNaN(ox)) ox = 0;
            if (double.IsNaN(oy)) oy = 0;

            // Left edge of drag widget snaps to right edge of other, and vice versa
            TrySnapX(x,     ox + ow, ref bestDx, ref snapX);
            TrySnapX(x + w, ox,      ref bestDx, ref snapX, -w);
            // Left edges aligned, right edges aligned
            TrySnapX(x,     ox,      ref bestDx, ref snapX);
            TrySnapX(x + w, ox + ow, ref bestDx, ref snapX, -w);

            // Top edge of drag widget snaps to bottom edge of other, and vice versa
            TrySnapY(y,     oy + oh, ref bestDy, ref snapY);
            TrySnapY(y + h, oy,      ref bestDy, ref snapY, -h);
            // Top edges aligned, bottom edges aligned
            TrySnapY(y,     oy,      ref bestDy, ref snapY);
            TrySnapY(y + h, oy + oh, ref bestDy, ref snapY, -h);
        }

        if (bestDx < SnapThreshold) AddGuideline(true,  snapX);
        if (bestDy < SnapThreshold) AddGuideline(false, snapY);

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
            StrokeThickness = 1,
            SnapsToDevicePixels = true
        };
        if (vertical)
        {
            line.X1 = line.X2 = pos;
            line.Y1 = 0;
            line.Y2 = ic.ActualHeight;
        }
        else
        {
            line.Y1 = line.Y2 = pos;
            line.X1 = 0;
            line.X2 = ic.ActualWidth;
        }
        var gc = FindGuidelineCanvas();
        gc?.Children.Add(line);
        _guidelines.Add(line);
    }

    private void ClearGuidelines()
    {
        var gc = FindGuidelineCanvas();
        if (gc != null)
            foreach (var line in _guidelines) gc.Children.Remove(line);
        _guidelines.Clear();
    }

    private Canvas? FindGuidelineCanvas()
    {
        var ic = FindItemsControl();
        if (ic?.Parent is not Grid grid) return null;
        foreach (UIElement child in grid.Children)
        {
            if (child is Canvas c && c.Tag as string == "GuidelineOverlay") return c;
        }
        return null;
    }

    // ---- Helpers ----

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

    // ---- BitPanel config dialog ----

    private void OnRequestBitPanelConfig(InstrumentWidget widget)
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
            if (DataContext is InstrumentPanelViewModel vm)
                vm.ForceTemplateRefresh(widget);
        }
    }
}

public class WidgetTemplateSelector : DataTemplateSelector
{
    public DataTemplate? NumericTemplate { get; set; }
    public DataTemplate? BarTemplate { get; set; }
    public DataTemplate? GaugeTemplate { get; set; }
    public DataTemplate? BooleanTemplate { get; set; }
    public DataTemplate? EnumTemplate { get; set; }
    public DataTemplate? BitPanelTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not InstrumentWidget w) return base.SelectTemplate(item, container);
        return w.Type switch
        {
            WidgetType.Numeric  => NumericTemplate,
            WidgetType.Bar      => BarTemplate,
            WidgetType.Gauge    => GaugeTemplate,
            WidgetType.Boolean  => BooleanTemplate,
            WidgetType.Enum     => EnumTemplate,
            WidgetType.BitPanel => BitPanelTemplate,
            _                   => NumericTemplate
        };
    }
}
