using CommunityToolkit.Mvvm.ComponentModel;
using CanLinConfig.Protocol;

namespace CanLinConfig.ViewModels;

public partial class DiagConfigViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    [ObservableProperty] private uint _canId = 0x7F0;
    [ObservableProperty] private ushort _intervalMs = 1000;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private byte _bus; // 0=CAN1, 1=CAN2
    [ObservableProperty] private ushort _canWatchdogMs;
    [ObservableProperty] private ushort _linWatchdogMs;
    [ObservableProperty] private byte _bulkTxRetries = 50;
    [ObservableProperty] private byte _bulkTxRetryDelayMs = 1;

    public string CanIdHex
    {
        get => $"0x{CanId:X3}";
        set
        {
            var s = value.Replace("0x", "").Replace("0X", "");
            if (uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint id) && id <= 0x7FF)
                CanId = id;
        }
    }

    public DiagConfigViewModel(MainViewModel main) { _main = main; }

    public async Task ReadFromDeviceAsync(ConfigProtocol proto)
    {
        var id = await proto.ReadParamAsync(ProtocolConstants.SectionDiag, 0, 0);
        if (id.Success && id.Value.Length >= 3)
            CanId = (uint)(id.Value[0] | (id.Value[1] << 8) | (id.Value[2] << 16));

        var iv = await proto.ReadParamAsync(ProtocolConstants.SectionDiag, 1, 0);
        if (iv.Success && iv.Value.Length >= 2)
            IntervalMs = (ushort)(iv.Value[0] | (iv.Value[1] << 8));

        var en = await proto.ReadParamAsync(ProtocolConstants.SectionDiag, 2, 0);
        if (en.Success && en.Value.Length >= 1) Enabled = en.Value[0] != 0;

        var b = await proto.ReadParamAsync(ProtocolConstants.SectionDiag, 3, 0);
        if (b.Success && b.Value.Length >= 1) Bus = b.Value[0];

        var cwdt = await proto.ReadParamAsync(ProtocolConstants.SectionDiag, 4, 0);
        if (cwdt.Success && cwdt.Value.Length >= 2)
            CanWatchdogMs = (ushort)(cwdt.Value[0] | (cwdt.Value[1] << 8));

        var lwdt = await proto.ReadParamAsync(ProtocolConstants.SectionDiag, 5, 0);
        if (lwdt.Success && lwdt.Value.Length >= 2)
            LinWatchdogMs = (ushort)(lwdt.Value[0] | (lwdt.Value[1] << 8));

        var btr = await proto.ReadParamAsync(ProtocolConstants.SectionDiag, 6, 0);
        if (btr.Success && btr.Value.Length >= 1 && btr.Value[0] > 0)
            BulkTxRetries = btr.Value[0];

        var btd = await proto.ReadParamAsync(ProtocolConstants.SectionDiag, 7, 0);
        if (btd.Success && btd.Value.Length >= 1 && btd.Value[0] > 0)
            BulkTxRetryDelayMs = btd.Value[0];
    }

    public async Task WriteToDeviceAsync(ConfigProtocol proto)
    {
        await proto.WriteParamAsync(ProtocolConstants.SectionDiag, 0, 0,
            [(byte)CanId, (byte)(CanId >> 8), (byte)(CanId >> 16)]);
        await proto.WriteParamAsync(ProtocolConstants.SectionDiag, 1, 0,
            [(byte)IntervalMs, (byte)(IntervalMs >> 8)]);
        await proto.WriteParamAsync(ProtocolConstants.SectionDiag, 2, 0,
            [(byte)(Enabled ? 1 : 0)]);
        await proto.WriteParamAsync(ProtocolConstants.SectionDiag, 3, 0, [Bus]);
        await proto.WriteParamAsync(ProtocolConstants.SectionDiag, 4, 0,
            [(byte)CanWatchdogMs, (byte)(CanWatchdogMs >> 8)]);
        await proto.WriteParamAsync(ProtocolConstants.SectionDiag, 5, 0,
            [(byte)LinWatchdogMs, (byte)(LinWatchdogMs >> 8)]);
        await proto.WriteParamAsync(ProtocolConstants.SectionDiag, 6, 0,
            [BulkTxRetries]);
        await proto.WriteParamAsync(ProtocolConstants.SectionDiag, 7, 0,
            [BulkTxRetryDelayMs]);
    }
}
