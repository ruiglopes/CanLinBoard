using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Protocol;

namespace CanLinConfig.ViewModels;

public partial class LogControlViewModel : ObservableObject
{
    private ConfigProtocol? _protocol;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private byte _loggerStatus;
    [ObservableProperty] private uint _entryCount;
    [ObservableProperty] private uint _wrapCount;
    [ObservableProperty] private ushort _flashErrors;
    [ObservableProperty] private string _statusText = "Idle";
    [ObservableProperty] private int _selectedModeIndex; // 0=Manual, 1=Continuous, 2=Triggered
    [ObservableProperty] private uint _dropCount;

    public string[] ModeNames { get; } = ["Manual", "Continuous", "Triggered"];

    // Trigger config
    [ObservableProperty] private byte _triggerBus;
    [ObservableProperty] private string _triggerId = "0x100";
    [ObservableProperty] private byte _triggerByteIndex;
    [ObservableProperty] private int _selectedTriggerOpIndex; // 0=Any,1=Eq,2=GT,3=LT,4=Mask
    [ObservableProperty] private byte _triggerValue;
    [ObservableProperty] private ushort _preTriggerKb = 64;
    [ObservableProperty] private ushort _postTriggerKb = 64;
    [ObservableProperty] private bool _isTriggeredMode;

    public string[] TriggerOpNames { get; } = ["Any Match", "Equals", "Greater Than", "Less Than", "Bit Mask"];
    public string[] BusNames { get; } = ["CAN1", "CAN2", "LIN1", "LIN2", "LIN3", "LIN4"];

    // Bus filter
    [ObservableProperty] private bool _logCan1 = true;
    [ObservableProperty] private bool _logCan2 = true;
    [ObservableProperty] private bool _logLin1 = true;
    [ObservableProperty] private bool _logLin2 = true;
    [ObservableProperty] private bool _logLin3 = true;
    [ObservableProperty] private bool _logLin4 = true;

    partial void OnSelectedModeIndexChanged(int value)
    {
        IsTriggeredMode = value == 2;
        _ = SendModeAsync();
    }

    private async Task SendModeAsync()
    {
        if (_protocol == null) return;
        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamMode, 0,
            [(byte)SelectedModeIndex]);
    }

    public void SetProtocol(ConfigProtocol? protocol)
    {
        _protocol = protocol;
        IsConnected = protocol != null;
        if (!IsConnected)
        {
            IsRecording = false;
            StatusText = "Disconnected";
#pragma warning disable MVVMTK0034 // Set backing field to avoid triggering SendModeAsync on disconnect
            _selectedModeIndex = 0;
#pragma warning restore MVVMTK0034
            OnPropertyChanged(nameof(SelectedModeIndex));
        }
        else
        {
            _ = RefreshStatusAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        if (_protocol == null) return;

        // Send bus mask first
        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamBusMask, 0,
            [BuildBusMask()]);

        // Send start command
        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamStateCmd, 0,
            [ProtocolConstants.LogCmdStart]);

        await RefreshStatusAsync();
    }

    private bool CanStart() => IsConnected && !IsRecording && !IsTriggeredMode;

    [RelayCommand(CanExecute = nameof(CanArm))]
    private async Task Arm()
    {
        if (_protocol == null) return;

        // Send trigger config first
        await SendTriggerConfigAsync();

        // Send bus mask
        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamBusMask, 0,
            [BuildBusMask()]);

        // Send arm command
        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamStateCmd, 0,
            [ProtocolConstants.LogCmdArm]);

        await RefreshStatusAsync();
    }

    private bool CanArm() => IsConnected && !IsRecording && IsTriggeredMode;

    private async Task SendTriggerConfigAsync()
    {
        if (_protocol == null) return;

        await _protocol.WriteParamAsync(ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamTriggerBus, 0, [TriggerBus]);

        // Parse trigger ID from hex string
        if (uint.TryParse(TriggerId.Replace("0x", "").Replace("0X", ""),
            System.Globalization.NumberStyles.HexNumber, null, out uint id))
        {
            await _protocol.WriteParamAsync(ProtocolConstants.SectionLog,
                ProtocolConstants.LogParamTriggerId, 0,
                [(byte)id, (byte)(id >> 8), (byte)(id >> 16), (byte)(id >> 24)]);
        }

        await _protocol.WriteParamAsync(ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamTriggerByte, 0, [TriggerByteIndex]);
        await _protocol.WriteParamAsync(ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamTriggerOp, 0, [(byte)SelectedTriggerOpIndex]);
        await _protocol.WriteParamAsync(ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamTriggerValue, 0, [TriggerValue]);
        await _protocol.WriteParamAsync(ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamPreTrigKb, 0,
            [(byte)PreTriggerKb, (byte)(PreTriggerKb >> 8)]);
        await _protocol.WriteParamAsync(ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamPostTrigKb, 0,
            [(byte)PostTriggerKb, (byte)(PostTriggerKb >> 8)]);
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        if (_protocol == null) return;

        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamStateCmd, 0,
            [ProtocolConstants.LogCmdStop]);

        await RefreshStatusAsync();
    }

    private bool CanStop() => IsConnected && IsRecording;

    [RelayCommand(CanExecute = nameof(CanErase))]
    private async Task EraseAll()
    {
        if (_protocol == null) return;

        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamStateCmd, 0,
            [ProtocolConstants.LogCmdEraseAll]);

        await RefreshStatusAsync();
    }

    private bool CanErase() => IsConnected && !IsRecording;

    [RelayCommand]
    private async Task RefreshStatus()
    {
        await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        if (_protocol == null) return;

        // Read status
        var status = await _protocol.ReadParamAsync(
            ProtocolConstants.SectionLog, ProtocolConstants.LogParamStatus, 0);
        if (status.Success && status.Value.Length > 0)
        {
            LoggerStatus = status.Value[0];
            IsRecording = LoggerStatus == ProtocolConstants.LogStateRecording
                       || LoggerStatus == ProtocolConstants.LogStateArmed
                       || LoggerStatus == ProtocolConstants.LogStateCapturing;
            StatusText = LoggerStatus switch
            {
                ProtocolConstants.LogStateIdle => "Idle",
                ProtocolConstants.LogStateRecording => SelectedModeIndex == 1 ? "Recording (Continuous)" : "Recording",
                ProtocolConstants.LogStateArmed => "Armed — waiting for trigger",
                ProtocolConstants.LogStateCapturing => "Triggered — capturing post-trigger data",
                ProtocolConstants.LogStateError => "Error — flash failures",
                _ => $"Unknown ({LoggerStatus})"
            };
        }

        // Read entry count (32-bit, split across sub=0 and sub=1)
        EntryCount = await ReadUint32ParamAsync(ProtocolConstants.LogParamEntryCount);

        // Read wrap count (32-bit, split)
        WrapCount = await ReadUint32ParamAsync(ProtocolConstants.LogParamWrapCount);

        // Read flash errors (16-bit, fits in single read)
        var errors = await _protocol.ReadParamAsync(
            ProtocolConstants.SectionLog, ProtocolConstants.LogParamFlashErrors, 0);
        if (errors.Success && errors.Value.Length >= 2)
        {
            FlashErrors = (ushort)(errors.Value[0] | (errors.Value[1] << 8));
        }

        // Read drop count (32-bit, split)
        DropCount = await ReadUint32ParamAsync(ProtocolConstants.LogParamDropCount);

        // Read current mode
        var mode = await _protocol.ReadParamAsync(
            ProtocolConstants.SectionLog, ProtocolConstants.LogParamMode, 0);
        if (mode.Success && mode.Value.Length > 0)
        {
#pragma warning disable MVVMTK0034 // Set backing field to avoid triggering SendModeAsync on refresh
            _selectedModeIndex = mode.Value[0];
#pragma warning restore MVVMTK0034
            OnPropertyChanged(nameof(SelectedModeIndex));
        }

        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        EraseAllCommand.NotifyCanExecuteChanged();
        ArmCommand.NotifyCanExecuteChanged();
    }

    partial void OnLogCan1Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnLogCan2Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnLogLin1Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnLogLin2Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnLogLin3Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnLogLin4Changed(bool value) => _ = SendBusMaskAsync();

    private byte BuildBusMask()
    {
        byte mask = 0;
        if (LogCan1) mask |= 0x01;
        if (LogCan2) mask |= 0x02;
        if (LogLin1) mask |= 0x04;
        if (LogLin2) mask |= 0x08;
        if (LogLin3) mask |= 0x10;
        if (LogLin4) mask |= 0x20;
        return mask;
    }

    private async Task SendBusMaskAsync()
    {
        if (_protocol == null || !IsRecording) return;
        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamBusMask, 0,
            [BuildBusMask()]);
    }

    /// <summary>
    /// Read a 32-bit param split across sub=0 (low16) and sub=1 (high16).
    /// </summary>
    private async Task<uint> ReadUint32ParamAsync(byte param)
    {
        if (_protocol == null) return 0;
        var lo = await _protocol.ReadParamAsync(
            ProtocolConstants.SectionLog, param, 0);
        var hi = await _protocol.ReadParamAsync(
            ProtocolConstants.SectionLog, param, 1);

        ushort loVal = (lo.Success && lo.Value.Length >= 2)
            ? (ushort)(lo.Value[0] | (lo.Value[1] << 8)) : (ushort)0;
        ushort hiVal = (hi.Success && hi.Value.Length >= 2)
            ? (ushort)(hi.Value[0] | (hi.Value[1] << 8)) : (ushort)0;

        return (uint)(loVal | (hiVal << 16));
    }
}
