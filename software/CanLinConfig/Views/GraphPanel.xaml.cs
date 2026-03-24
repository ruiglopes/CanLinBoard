using System.Windows.Controls;
using System.Windows.Threading;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Views;

public partial class GraphPanel : UserControl
{
    private DispatcherTimer? _refreshTimer;
    private bool _needsRefresh;

    public GraphPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is GraphPanelViewModel oldVm)
            oldVm.PlotNeedsRefresh -= OnPlotNeedsRefresh;

        _refreshTimer?.Stop();

        if (e.NewValue is GraphPanelViewModel newVm)
        {
            newVm.PlotNeedsRefresh += OnPlotNeedsRefresh;
            SetupPlot();
        }
    }

    private void SetupPlot()
    {
        WpfPlot.Plot.Title("Signal Graph");
        WpfPlot.Plot.XLabel("Time");
        WpfPlot.Plot.YLabel("Value");
        WpfPlot.Refresh();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _refreshTimer.Tick += (_, _) => RefreshPlot();
        _refreshTimer.Start();
    }

    private void OnPlotNeedsRefresh(object? sender, EventArgs e) => _needsRefresh = true;

    private void RefreshPlot()
    {
        if (!_needsRefresh || DataContext is not GraphPanelViewModel vm) return;
        _needsRefresh = false;

        WpfPlot.Plot.PlottableList.Clear();
        foreach (var trace in vm.Traces)
        {
            var (timestamps, values) = trace.GetData();
            if (timestamps.Length < 2) continue;
            var scatter = WpfPlot.Plot.Add.Scatter(timestamps, values);
            scatter.LegendText = $"{trace.DisplayName} [{trace.Unit}]";
        }
        WpfPlot.Plot.Axes.DateTimeTicksBottom();

        // Apply time window — show only the last N seconds
        var now = DateTime.Now.ToOADate();
        var windowStart = DateTime.Now.AddSeconds(-vm.TimeWindowSeconds).ToOADate();
        WpfPlot.Plot.Axes.SetLimitsX(windowStart, now);

        WpfPlot.Plot.ShowLegend();
        WpfPlot.Refresh();
    }
}
