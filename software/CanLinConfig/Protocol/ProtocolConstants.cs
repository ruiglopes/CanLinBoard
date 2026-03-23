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

    // Bus Monitor CAN IDs
    public const uint MonitorHeaderId = 0x604;
    public const uint MonitorDataId = 0x605;

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
    public const byte SectionMonitor = 0x06;

    // Monitor param indices (SectionMonitor READ_PARAM/WRITE_PARAM)
    public const byte MonitorParamEnable = 0;
    public const byte MonitorParamBusMask = 1;
    public const byte MonitorParamFilterMode = 2;
    public const byte MonitorParamDropCount = 3;

    // Monitor filter modes
    public const byte MonitorFilterNone = 0;
    public const byte MonitorFilterWhitelist = 1;
    public const byte MonitorFilterBlacklist = 2;

    // Monitor limits
    public const int MonitorMaxFilterIds = 32;

    // ---- Logger (SectionLog = 0x07) ----
    public const byte SectionLog = 0x07;

    // Logger param indices (single-byte params)
    public const byte LogParamMode = 0;
    public const byte LogParamBusMask = 1;
    public const byte LogParamStateCmd = 2;
    public const byte LogParamStatus = 3;

    // Logger param indices (32-bit params, read via sub=0/sub=1 for low/high 16 bits)
    public const byte LogParamEntryCount = 4;
    public const byte LogParamWrapCount = 5;
    public const byte LogParamWriteOffset = 6;
    public const byte LogParamFlashErrors = 7;
    public const byte LogParamDropCount = 8;

    // Logger states
    public const byte LogStateIdle = 0;
    public const byte LogStateRecording = 1;
    public const byte LogStateError = 4;

    // Logger modes
    public const byte LogModeManual = 0;
    public const byte LogModeContinuous = 1;
    public const byte LogModeTriggered = 2;

    // Logger states
    public const byte LogStateArmed = 2;
    public const byte LogStateCapturing = 3;

    // Logger state commands
    public const byte LogCmdStop = 0;
    public const byte LogCmdStart = 1;
    public const byte LogCmdArm = 2;
    public const byte LogCmdEraseAll = 0xFF;

    // Trigger params
    public const byte LogParamTriggerBus = 9;
    public const byte LogParamTriggerId = 10;
    public const byte LogParamTriggerByte = 11;
    public const byte LogParamTriggerOp = 12;
    public const byte LogParamTriggerValue = 13;
    public const byte LogParamPreTrigKb = 14;
    public const byte LogParamPostTrigKb = 15;

    // Chunked log read
    public const byte CmdLogReadChunk = 0x24;
    public const ushort LogChunkSize = 4096;

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

    // Device param indices (SectionDevice READ_PARAM)
    public const byte DeviceParamRoutingRuleSize = 0;
    public const byte DeviceParamLinEntrySize = 1;
    public const byte DeviceParamLinTableSize = 2;

    // Expected struct sizes (must match firmware _Static_assert values)
    public const int ExpectedRoutingRuleSize = 64;
    public const int ExpectedLinEntrySize = 16;
    public const int ExpectedLinTableSize = 258;
}
