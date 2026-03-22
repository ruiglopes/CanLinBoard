namespace CanBus;

public class BootloaderInfo
{
    public int VersionMajor { get; set; }
    public int VersionMinor { get; set; }
    public int VersionPatch { get; set; }
    public int ProtoVersion { get; set; }
    public int PageSize { get; set; } = 256;
    public uint DebugCanId { get; set; } = 0x7FF;
    public bool AuthRequired { get; set; }
    public byte[]? Nonce { get; set; }
    public DeviceIdentity? DeviceIdentity { get; set; }

    public bool IsVersionValid => VersionMajor != 0 || VersionMinor != 0 || VersionPatch != 0;
    public string VersionString => $"{VersionMajor}.{VersionMinor}.{VersionPatch}";
}
