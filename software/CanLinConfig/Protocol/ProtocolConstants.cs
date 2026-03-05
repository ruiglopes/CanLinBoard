namespace CanLinConfig.Protocol;

/// <summary>
/// Mirror of firmware config_protocol.h and board_config.h CAN IDs.
/// </summary>
public static class ProtocolConstants
{
    // CAN IDs
    public const uint ConfigCmdId = 0x600;
    public const uint ConfigRespId = 0x601;
    public const uint ConfigDataId = 0x602;
    public const uint ConfigBulkRespId = 0x603;

    // Diagnostics CAN IDs
    public const uint DiagStatusId = 0x7F0;
    public const uint DiagCanStatsId = 0x7F1;
    public const uint DiagLinStatsId = 0x7F2;
    public const uint DiagCrashId = 0x7F3;
    public const uint DiagSysHealthId = 0x7F4;

    // Bootloader
    public const uint BlCmdId = 0x700;

    // Command codes
    public const byte CmdConnect = 0x01;
    public const byte CmdSave = 0x02;
    public const byte CmdDefaults = 0x03;
    public const byte CmdReboot = 0x04;
    public const byte CmdEnterBootloader = 0x05;
    public const byte CmdGetStatus = 0x06;
    public const byte CmdReadParam = 0x10;
    public const byte CmdWriteParam = 0x11;
    public const byte CmdBulkStart = 0x20;
    public const byte CmdBulkEnd = 0x21;
    public const byte CmdBulkRead = 0x22;
    public const byte CmdBulkReadData = 0x23;

    // Section IDs
    public const byte SectionCan = 0x00;
    public const byte SectionLin = 0x01;
    public const byte SectionRouting = 0x02;
    public const byte SectionDiag = 0x03;
    public const byte SectionProfiles = 0x04;
    public const byte SectionDevice = 0x05;

    // Response status codes
    public const byte StatusOk = 0x00;
    public const byte StatusUnknownCmd = 0x01;
    public const byte StatusInvalidParam = 0x02;
    public const byte StatusCrcMismatch = 0x03;
    public const byte StatusNvmError = 0x04;
    public const byte StatusBusy = 0x05;

    // Bootloader unlock key
    public const uint ResetUnlockKey = 0xB007CAFE;

    // Limits
    public const int MaxRoutingRules = 32;
    public const int MaxByteMappings = 8;
    public const int MaxScheduleEntries = 16;
    public const int LinChannelCount = 4;
}
