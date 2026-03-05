using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Adapters;
using CanLinConfig.Protocol;

namespace CanLinConfig.ViewModels;

public partial class DiagnosticsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Dispatcher _dispatcher;

    // System Status
    [ObservableProperty] private string _systemState = "---";
    [ObservableProperty] private string _uptime = "---";
    [ObservableProperty] private string _mcuTemp = "---";
    [ObservableProperty] private string _resetReason = "---";

    // CAN Statistics
    [ObservableProperty] private uint _can1RxCount;
    [ObservableProperty] private byte _can1ErrorCount;
    [ObservableProperty] private uint _can2RxCount;
    [ObservableProperty] private byte _can2ErrorCount;
    [ObservableProperty] private uint _gwRoutedCount;

    // LIN Statistics
    [ObservableProperty] private byte _lin1RxCount;
    [ObservableProperty] private byte _lin1ErrorCount;
    [ObservableProperty] private byte _lin2RxCount;
    [ObservableProperty] private byte _lin2ErrorCount;
    [ObservableProperty] private byte _lin3RxCount;
    [ObservableProperty] private byte _lin3ErrorCount;
    [ObservableProperty] private byte _lin4RxCount;
    [ObservableProperty] private byte _lin4ErrorCount;

    // System Health
    [ObservableProperty] private byte _heapFreeKb;
    [ObservableProperty] private byte _minStackWatermark;
    [ObservableProperty] private byte _wdtTimeoutMask;

    // Crash Report
    [ObservableProperty] private string _crashInfo = "None";

    // Frame Monitor
    public ObservableCollection<FrameLogEntry> FrameLog { get; } = [];
    public ICollectionView FilteredFrameLog { get; }
    [ObservableProperty] private bool _isMonitorPaused;
    [ObservableProperty] private string _idFilter = "";
    private int _maxFrameLog = 1000;

    public DiagnosticsViewModel(MainViewModel main)
    {
        _main = main;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        FilteredFrameLog = CollectionViewSource.GetDefaultView(FrameLog);
        FilteredFrameLog.Filter = FilterFrame;
    }

    partial void OnIdFilterChanged(string value)
    {
        FilteredFrameLog.Refresh();
    }

    private bool FilterFrame(object obj)
    {
        if (string.IsNullOrEmpty(IdFilter) || obj is not FrameLogEntry entry)
            return true;
        var filterText = IdFilter.Replace("0x", "").Replace("0X", "");
        if (uint.TryParse(filterText, System.Globalization.NumberStyles.HexNumber, null, out uint filterId))
            return entry.RawId == filterId;
        return true;
    }

    public void OnRawFrame(CanFrame frame)
    {
        _dispatcher.BeginInvoke(() =>
        {
            DecodeFrame(frame);
            if (!IsMonitorPaused)
                AddFrameLog(frame);
        });
    }

    private void DecodeFrame(CanFrame frame)
    {
        switch (frame.Id)
        {
            case ProtocolConstants.DiagStatusId when frame.Dlc == 8:
                DecodeStatusHeartbeat(frame);
                break;
            case ProtocolConstants.DiagStatusId when frame.Dlc == 6:
                // Boot version frame - ignore for live view
                break;
            case ProtocolConstants.DiagCanStatsId:
                DecodeCanStats(frame);
                break;
            case ProtocolConstants.DiagLinStatsId:
                DecodeLinStats(frame);
                break;
            case ProtocolConstants.DiagCrashId:
                DecodeCrashReport(frame);
                break;
            case ProtocolConstants.DiagSysHealthId:
                DecodeSysHealth(frame);
                break;
        }
    }

    private void DecodeStatusHeartbeat(CanFrame f)
    {
        // Big-endian: bytes 0-3 = uptime_s
        uint uptime = (uint)((f.Data[0] << 24) | (f.Data[1] << 16) | (f.Data[2] << 8) | f.Data[3]);
        var ts = TimeSpan.FromSeconds(uptime);
        Uptime = uptime < 3600 ? $"{ts.Minutes}m {ts.Seconds}s" : $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";

        SystemState = f.Data[4] switch
        {
            0 => "BOOT", 1 => "OK", 2 => "WARN", 3 => "ERROR", _ => $"?({f.Data[4]})"
        };

        McuTemp = $"{(sbyte)f.Data[6]} C";
        ResetReason = f.Data[7] switch
        {
            0 => "Power On", 1 => "Watchdog", 2 => "Crash", 3 => "Unknown", _ => $"?({f.Data[7]})"
        };
    }

    private void DecodeCanStats(CanFrame f)
    {
        Can1RxCount = (uint)((f.Data[0] << 8) | f.Data[1]);
        Can1ErrorCount = f.Data[2];
        Can2RxCount = (uint)((f.Data[3] << 8) | f.Data[4]);
        Can2ErrorCount = f.Data[5];
        GwRoutedCount = (uint)((f.Data[6] << 8) | f.Data[7]);
    }

    private void DecodeLinStats(CanFrame f)
    {
        Lin1RxCount = f.Data[0]; Lin1ErrorCount = f.Data[1];
        Lin2RxCount = f.Data[2]; Lin2ErrorCount = f.Data[3];
        Lin3RxCount = f.Data[4]; Lin3ErrorCount = f.Data[5];
        Lin4RxCount = f.Data[6]; Lin4ErrorCount = f.Data[7];
    }

    private void DecodeCrashReport(CanFrame f)
    {
        byte faultType = f.Data[0];
        uint pc = (uint)((f.Data[1] << 24) | (f.Data[2] << 16) | (f.Data[3] << 8) | f.Data[4]);
        ushort crashUptime = (ushort)((f.Data[5] << 8) | f.Data[6]);
        char taskChar = (char)f.Data[7];

        string faultName = faultType switch
        {
            0 => "None", 1 => "HardFault", 2 => "StackOverflow",
            3 => "MallocFail", 4 => "AssertFail", 5 => "Watchdog", _ => $"?({faultType})"
        };

        CrashInfo = faultType == 0 ? "None" : $"{faultName} at PC=0x{pc:X8}, uptime={crashUptime}s, task='{taskChar}'";
    }

    private void DecodeSysHealth(CanFrame f)
    {
        HeapFreeKb = f.Data[0];
        MinStackWatermark = f.Data[1];
        WdtTimeoutMask = f.Dlc >= 3 ? f.Data[2] : (byte)0;
    }

    private void AddFrameLog(CanFrame frame)
    {
        FrameLog.Add(new FrameLogEntry(frame));
        while (FrameLog.Count > _maxFrameLog)
            FrameLog.RemoveAt(0);
    }

    [RelayCommand]
    private void ClearLog() => FrameLog.Clear();

    [RelayCommand]
    private void TogglePause() => IsMonitorPaused = !IsMonitorPaused;

    public void StopMonitoring()
    {
        // Cleanup if needed
    }
}

public class FrameLogEntry
{
    public string Time { get; }
    public string Id { get; }
    public uint RawId { get; }
    public byte Dlc { get; }
    public string DataHex { get; }

    public FrameLogEntry(CanFrame frame)
    {
        Time = frame.Timestamp.ToString("HH:mm:ss.fff");
        Id = $"0x{frame.Id:X3}";
        RawId = frame.Id;
        Dlc = frame.Dlc;
        DataHex = string.Join(" ", frame.Data.Take(frame.Dlc).Select(b => b.ToString("X2")));
    }
}
