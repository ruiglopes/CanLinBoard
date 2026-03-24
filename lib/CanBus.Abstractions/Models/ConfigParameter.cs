using System;

namespace CanBus;

public enum ConfigParamId : byte
{
    Bitrate = 0x01,
    CanIdMode = 0x02,
    DebugEnabled = 0x03,
    DeviceId = 0x04,
    WriteCount = 0x05,
    CanWaitEnabled = 0x06,
    CanWaitTimeout = 0x07,
    DebugCanId = 0x08,
    MinAppVersion = 0x09,
    AntiRollback = 0x0A,
    AuthRequired = 0x0B,
    FlashErrors = 0x0C,
    FastBoot = 0x0D,
    NodeId = 0x0E,
    BootCount = 0x0F,
    BootCountEnabled = 0x10,
}

public enum ConfigStatus : byte
{
    Ok = 0x00,
    Unknown = 0x01,
    FlashError = 0x02,
    ReadOnly = 0x03,
}

public class ConfigParameter
{
    public ConfigParamId Id { get; set; }
    public string Name { get; set; } = "";
    public byte[] RawValue { get; set; } = Array.Empty<byte>();
    public bool IsReadOnly { get; set; }
    public ConfigStatus LastStatus { get; set; }

    public uint AsUInt32 =>
        RawValue.Length >= 4 ? BitConverter.ToUInt32(RawValue, 0) : (uint)(RawValue.Length > 0 ? RawValue[0] : 0);

    public byte AsByte => RawValue.Length > 0 ? RawValue[0] : (byte)0;

    public static byte[] FromUInt32(uint value) => BitConverter.GetBytes(value);
    public static byte[] FromByte(byte value) => new[] { value };
    public static byte[] FromUInt16(ushort value) => BitConverter.GetBytes(value);

    public static ConfigParameter[] CreateDefaults() => new[]
    {
        new ConfigParameter { Id = ConfigParamId.Bitrate, Name = "CAN Bitrate" },
        new ConfigParameter { Id = ConfigParamId.CanIdMode, Name = "CAN ID Mode" },
        new ConfigParameter { Id = ConfigParamId.DebugEnabled, Name = "Debug Enabled" },
        new ConfigParameter { Id = ConfigParamId.DeviceId, Name = "Device ID" },
        new ConfigParameter { Id = ConfigParamId.WriteCount, Name = "Write Count", IsReadOnly = true },
        new ConfigParameter { Id = ConfigParamId.CanWaitEnabled, Name = "CAN Wait Enabled" },
        new ConfigParameter { Id = ConfigParamId.CanWaitTimeout, Name = "CAN Wait Timeout" },
        new ConfigParameter { Id = ConfigParamId.DebugCanId, Name = "Debug CAN ID" },
        new ConfigParameter { Id = ConfigParamId.MinAppVersion, Name = "Min App Version", IsReadOnly = true },
        new ConfigParameter { Id = ConfigParamId.AntiRollback, Name = "Anti-Rollback" },
        new ConfigParameter { Id = ConfigParamId.AuthRequired, Name = "Auth Required" },
        new ConfigParameter { Id = ConfigParamId.FlashErrors, Name = "Flash Errors" },
        new ConfigParameter { Id = ConfigParamId.FastBoot, Name = "Fast Boot" },
        new ConfigParameter { Id = ConfigParamId.NodeId, Name = "Node ID" },
        new ConfigParameter { Id = ConfigParamId.BootCount, Name = "Boot Count", IsReadOnly = true },
        new ConfigParameter { Id = ConfigParamId.BootCountEnabled, Name = "Boot Count Enabled" },
    };
}
