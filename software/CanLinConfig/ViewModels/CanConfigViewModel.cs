using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Protocol;

namespace CanLinConfig.ViewModels;

public partial class CanBusViewModel : ObservableObject
{
    [ObservableProperty] private uint _bitrate = 500000;
    [ObservableProperty] private bool _termination;
    [ObservableProperty] private bool _enabled;
    public int BusIndex { get; }
    public string BusName => BusIndex == 0 ? "CAN1" : "CAN2";
    public bool CanDisable => BusIndex == 1; // CAN1 always on

    public CanBusViewModel(int busIndex) { BusIndex = busIndex; }
}

public partial class CanConfigViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public CanBusViewModel Can1 { get; } = new(0);
    public CanBusViewModel Can2 { get; } = new(1);

    public CanConfigViewModel(MainViewModel main) { _main = main; }

    public async Task ReadFromDeviceAsync(ConfigProtocol proto)
    {
        for (int bus = 0; bus < 2; bus++)
        {
            var vm = bus == 0 ? Can1 : Can2;
            var br = await proto.ReadParamAsync(ProtocolConstants.SectionCan, 0, (byte)bus);
            if (br.Success && br.Value.Length >= 3)
                vm.Bitrate = (uint)(br.Value[0] | (br.Value[1] << 8) | (br.Value[2] << 16));

            var term = await proto.ReadParamAsync(ProtocolConstants.SectionCan, 1, (byte)bus);
            if (term.Success && term.Value.Length >= 1) vm.Termination = term.Value[0] != 0;

            var en = await proto.ReadParamAsync(ProtocolConstants.SectionCan, 2, (byte)bus);
            if (en.Success && en.Value.Length >= 1) vm.Enabled = en.Value[0] != 0;
        }
    }

    public async Task WriteToDeviceAsync(ConfigProtocol proto)
    {
        for (int bus = 0; bus < 2; bus++)
        {
            var vm = bus == 0 ? Can1 : Can2;
            await proto.WriteParamAsync(ProtocolConstants.SectionCan, 0, (byte)bus,
                [(byte)vm.Bitrate, (byte)(vm.Bitrate >> 8), (byte)(vm.Bitrate >> 16)]);
            await proto.WriteParamAsync(ProtocolConstants.SectionCan, 1, (byte)bus,
                [(byte)(vm.Termination ? 1 : 0)]);
            await proto.WriteParamAsync(ProtocolConstants.SectionCan, 2, (byte)bus,
                [(byte)(vm.Enabled ? 1 : 0)]);
        }
    }
}
