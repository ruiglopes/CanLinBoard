using System;

namespace CanBus;

public class EnumeratedDevice
{
    public byte[] Uid { get; set; } = Array.Empty<byte>();
    public string UidHex => Uid.Length > 0 ? BitConverter.ToString(Uid).Replace("-", ":") : "";
    public string DisplayName => $"UID {UidHex}";
    public override string ToString() => DisplayName;
}
