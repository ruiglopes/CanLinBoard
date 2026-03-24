using CommunityToolkit.Mvvm.ComponentModel;
using CanLinConfig.Protocol;
using CanLinConfig.Services;

namespace CanLinConfig.ViewModels;

public partial class DataLoggerViewModel : ObservableObject
{
    public LogControlViewModel LogControl { get; }
    public LogDownloadViewModel LogDownload { get; }
    public LogReplayViewModel LogReplay { get; }

    public DataLoggerViewModel()
    {
        LogControl = new LogControlViewModel();
        LogDownload = new LogDownloadViewModel();
        LogReplay = new LogReplayViewModel();
    }

    public void SetProtocol(ConfigProtocol? protocol, BusDataService? busDataService)
    {
        LogControl.SetProtocol(protocol);
        LogDownload.SetProtocol(protocol, busDataService);
        LogReplay.SetBusDataService(busDataService);
    }
}
