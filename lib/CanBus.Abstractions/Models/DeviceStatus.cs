namespace CanBus;

public class DeviceStatus
{
    public byte State { get; set; }
    public byte LastError { get; set; }
    public uint BytesReceived { get; set; }
    public byte ResetReason { get; set; }

    private static readonly string[] StateNames =
        { "IDLE", "AUTH_PENDING", "CONNECTED", "ERASING", "RECEIVING", "COMPLETE" };

    public string StateName => State < StateNames.Length ? StateNames[State] : $"0x{State:X2}";

    public string LastErrorName => LastError switch
    {
        0x00 => "None",
        0x01 => "Generic",
        0x02 => "Wrong state",
        0x03 => "Bad address",
        0x04 => "Bad length",
        0x05 => "Flash error",
        0x06 => "CRC mismatch",
        0x08 => "Bad sequence",
        0x09 => "Bad image",
        0x0A => "Auth required",
        0x0B => "Auth failed",
        _ => $"0x{LastError:X2}"
    };

    public string ResetReasonName => ProtocolConstants.FormatResetReason(ResetReason);

    public string BytesReceivedFormatted => BytesReceived > 1024
        ? $"{BytesReceived:N0} ({BytesReceived / 1024.0:F1} KB)"
        : $"{BytesReceived:N0}";
}
