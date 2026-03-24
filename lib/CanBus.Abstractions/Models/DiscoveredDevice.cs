using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace CanBus;

public class DiscoveredDevice : INotifyPropertyChanged
{
    private BootloaderInfo? _info;

    public byte NodeId { get; set; }
    public byte[]? Uid { get; set; }
    public uint CmdCanId => 0x700u + NodeId * 4u;
    public uint RspCanId => CmdCanId + 1;
    public uint DataCanId => CmdCanId + 2;

    public BootloaderInfo? Info
    {
        get => _info;
        set
        {
            _info = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public string UidHex => Uid != null ? BitConverter.ToString(Uid).Replace("-", ":") : "";
    public bool HasUid => Uid != null && Uid.Length == 6;

    public string DisplayName
    {
        get
        {
            var id = HasUid ? $"UID {UidHex}" : $"Node {NodeId}";
            if (Info == null)
                return id;
            var version = Info.IsVersionValid ? $"v{Info.VersionString}" : null;
            var devId = Info.DeviceIdentity?.UniqueIdHex;
            var detail = string.Join(" ", new[] { version, devId != null ? $"[{devId}]" : null }.Where(s => s != null));
            return detail.Length > 0 ? $"{id}: {detail}" : id;
        }
    }

    public override string ToString() => DisplayName;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
