using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Models;
using CanLinConfig.Services;
using CanLinConfig.Services.Export;
using Microsoft.Win32;

namespace CanLinConfig.ViewModels;

public partial class BusMonitorViewModel : ObservableObject
{
    private readonly BusDataService _busDataService;
    private readonly Dispatcher _dispatcher;

    public TracePanelViewModel Trace { get; }
    public SignalPanelViewModel Signals { get; }
    public GraphPanelViewModel Graph { get; }

    [ObservableProperty] private string _can1DbPath = "(none)";
    [ObservableProperty] private string _can2DbPath = "(none)";
    [ObservableProperty] private string _lin1DbPath = "(none)";
    [ObservableProperty] private string _lin2DbPath = "(none)";
    [ObservableProperty] private string _lin3DbPath = "(none)";
    [ObservableProperty] private string _lin4DbPath = "(none)";

    public BusMonitorViewModel(BusDataService busDataService)
    {
        _busDataService = busDataService;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        Trace = new TracePanelViewModel();
        Signals = new SignalPanelViewModel();
        Graph = new GraphPanelViewModel();

        _busDataService.FrameReceived += OnFrameReceived;
        _busDataService.SignalsDecoded += OnSignalsDecoded;
        Signals.AddToGraphRequested += OnAddToGraph;
    }

    private void OnFrameReceived(object? sender, BusFrame frame)
    {
        var msgName = _busDataService.DatabaseManager.GetMessageName(frame.SourceBus, frame.Id);
        _dispatcher.BeginInvoke(() => Trace.AddFrame(frame, msgName));
    }

    private void OnSignalsDecoded(object? sender, IReadOnlyList<SignalValue> signals)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Signals.UpdateSignals(signals);
            Graph.OnSignalValues(signals);
        });
    }

    private void OnAddToGraph(object? sender, SignalEntry entry)
    {
        Graph.AddSignal(entry.MessageKey, entry.Name, entry.Unit);
    }

    [RelayCommand]
    private void ExportFrames()
    {
        var frames = _busDataService.FrameHistory;
        if (frames.Count == 0)
        {
            System.Windows.MessageBox.Show("No frames to export.", "Export",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv|ASC Files (*.asc)|*.asc|BLF Files (*.blf)|*.blf|All Files (*.*)|*.*",
            Title = "Export Frames",
            FileName = $"capture_{DateTime.Now:yyyyMMdd_HHmmss}"
        };
        if (dlg.ShowDialog() != true) return;

        IFrameExporter exporter = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant() switch
        {
            ".asc" => new AscExporter(),
            ".blf" => new BlfExporter(),
            _ => new CsvExporter()
        };

        try
        {
            using var fs = File.Create(dlg.FileName);
            exporter.Export(fs, frames, _busDataService.DatabaseManager);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Export failed: {ex.Message}", "Export Error",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    [RelayCommand] private void AssignCan1Db() => AssignDb(BusFrame.Bus.CAN1, v => Can1DbPath = v);
    [RelayCommand] private void ClearCan1Db() => ClearDb(BusFrame.Bus.CAN1, () => Can1DbPath = "(none)");
    [RelayCommand] private void AssignCan2Db() => AssignDb(BusFrame.Bus.CAN2, v => Can2DbPath = v);
    [RelayCommand] private void ClearCan2Db() => ClearDb(BusFrame.Bus.CAN2, () => Can2DbPath = "(none)");
    [RelayCommand] private void AssignLin1Db() => AssignDb(BusFrame.Bus.LIN1, v => Lin1DbPath = v);
    [RelayCommand] private void ClearLin1Db() => ClearDb(BusFrame.Bus.LIN1, () => Lin1DbPath = "(none)");
    [RelayCommand] private void AssignLin2Db() => AssignDb(BusFrame.Bus.LIN2, v => Lin2DbPath = v);
    [RelayCommand] private void ClearLin2Db() => ClearDb(BusFrame.Bus.LIN2, () => Lin2DbPath = "(none)");
    [RelayCommand] private void AssignLin3Db() => AssignDb(BusFrame.Bus.LIN3, v => Lin3DbPath = v);
    [RelayCommand] private void ClearLin3Db() => ClearDb(BusFrame.Bus.LIN3, () => Lin3DbPath = "(none)");
    [RelayCommand] private void AssignLin4Db() => AssignDb(BusFrame.Bus.LIN4, v => Lin4DbPath = v);
    [RelayCommand] private void ClearLin4Db() => ClearDb(BusFrame.Bus.LIN4, () => Lin4DbPath = "(none)");

    private void AssignDb(BusFrame.Bus bus, Action<string> setPath)
    {
        var path = BrowseDbFile();
        if (path == null) return;
        _busDataService.DatabaseManager.AssignDatabase(bus, path);
        setPath(System.IO.Path.GetFileName(path));
    }

    private void ClearDb(BusFrame.Bus bus, Action resetPath)
    {
        _busDataService.DatabaseManager.RemoveDatabase(bus);
        resetPath();
    }

    private static string? BrowseDbFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Database Files (*.dbc;*.ldf)|*.dbc;*.ldf|DBC Files (*.dbc)|*.dbc|LDF Files (*.ldf)|*.ldf|All Files (*.*)|*.*",
            Title = "Select Bus Database"
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public void Cleanup()
    {
        _busDataService.FrameReceived -= OnFrameReceived;
        _busDataService.SignalsDecoded -= OnSignalsDecoded;
    }
}
