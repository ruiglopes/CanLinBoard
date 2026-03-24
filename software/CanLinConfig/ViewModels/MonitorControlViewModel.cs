using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Protocol;
using CanLinConfig.Services;

namespace CanLinConfig.ViewModels;

public partial class MonitorControlViewModel : ObservableObject
{
    private ConfigProtocol? _protocol;
    private MonitorFrameDecoder? _decoder;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _monitorCan1; // off by default — tool sees CAN1 natively
    [ObservableProperty] private bool _monitorCan2 = true;
    [ObservableProperty] private bool _monitorLin1 = true;
    [ObservableProperty] private bool _monitorLin2 = true;
    [ObservableProperty] private bool _monitorLin3 = true;
    [ObservableProperty] private bool _monitorLin4 = true;
    [ObservableProperty] private uint _sequenceGaps;
    [ObservableProperty] private uint _dropCount;
    [ObservableProperty] private bool _isConnected;

    public void SetProtocol(ConfigProtocol? protocol, MonitorFrameDecoder? decoder)
    {
        _protocol = protocol;
        _decoder = decoder;
        IsConnected = protocol != null;
        if (!IsConnected)
        {
            IsEnabled = false;
            SequenceGaps = 0;
            DropCount = 0;
        }
    }

    public void UpdateStats()
    {
        if (_decoder != null)
            SequenceGaps = _decoder.SequenceGapCount;
    }

    partial void OnIsEnabledChanged(bool value) => _ = SendEnableAsync(value);

    partial void OnMonitorCan1Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnMonitorCan2Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnMonitorLin1Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnMonitorLin2Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnMonitorLin3Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnMonitorLin4Changed(bool value) => _ = SendBusMaskAsync();

    private byte BuildBusMask()
    {
        byte mask = 0;
        if (MonitorCan1) mask |= 0x01;
        if (MonitorCan2) mask |= 0x02;
        if (MonitorLin1) mask |= 0x04;
        if (MonitorLin2) mask |= 0x08;
        if (MonitorLin3) mask |= 0x10;
        if (MonitorLin4) mask |= 0x20;
        return mask;
    }

    private async Task SendEnableAsync(bool enabled)
    {
        if (_protocol == null) return;
        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionMonitor,
            ProtocolConstants.MonitorParamEnable, 0,
            [(byte)(enabled ? 1 : 0)]);
    }

    private async Task SendBusMaskAsync()
    {
        if (_protocol == null) return;
        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionMonitor,
            ProtocolConstants.MonitorParamBusMask, 0,
            [BuildBusMask()]);
    }

    [RelayCommand]
    private async Task RefreshDropCount()
    {
        if (_protocol == null) return;
        var result = await _protocol.ReadParamAsync(
            ProtocolConstants.SectionMonitor,
            ProtocolConstants.MonitorParamDropCount, 0);
        if (result.Success && result.Value.Length > 0)
            DropCount = result.Value[0];
    }
}
