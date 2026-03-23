using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Models;
using CanLinConfig.Services;
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
    private void AssignCan1Db()
    {
        var path = BrowseDbcFile();
        if (path == null) return;
        _busDataService.DatabaseManager.AssignDatabase(BusFrame.Bus.CAN1, path);
        Can1DbPath = System.IO.Path.GetFileName(path);
    }

    [RelayCommand]
    private void AssignCan2Db()
    {
        var path = BrowseDbcFile();
        if (path == null) return;
        _busDataService.DatabaseManager.AssignDatabase(BusFrame.Bus.CAN2, path);
        Can2DbPath = System.IO.Path.GetFileName(path);
    }

    [RelayCommand]
    private void ClearCan1Db()
    {
        _busDataService.DatabaseManager.RemoveDatabase(BusFrame.Bus.CAN1);
        Can1DbPath = "(none)";
    }

    [RelayCommand]
    private void ClearCan2Db()
    {
        _busDataService.DatabaseManager.RemoveDatabase(BusFrame.Bus.CAN2);
        Can2DbPath = "(none)";
    }

    private static string? BrowseDbcFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "DBC Files (*.dbc)|*.dbc|All Files (*.*)|*.*",
            Title = "Select CAN Database"
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public void Cleanup()
    {
        _busDataService.FrameReceived -= OnFrameReceived;
        _busDataService.SignalsDecoded -= OnSignalsDecoded;
    }
}
