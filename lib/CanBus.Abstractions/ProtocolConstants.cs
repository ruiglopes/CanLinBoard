namespace CanBus;

public static class ProtocolConstants
{
    // CAN IDs — defaults for node_id 0
    public const uint CanIdCmdDefault = 0x700;
    public const uint CanIdRespDefault = 0x701;
    public const uint CanIdDataDefault = 0x702;
    public const uint CanIdDebugDefault = 0x7FF;

    // Command IDs
    public const byte CmdConnect = 0x01;
    public const byte CmdErase = 0x02;
    public const byte CmdData = 0x03;
    public const byte CmdVerify = 0x04;
    public const byte CmdReset = 0x05;
    public const byte CmdConfigRead = 0x06;
    public const byte CmdConfigWrite = 0x07;
    public const byte CmdDebugToggle = 0x08;
    public const byte CmdSelfUpdate = 0x09;
    public const byte CmdGetStatus = 0x0A;
    public const byte CmdAuth = 0x0B;
    public const byte CmdProvisionKey = 0x0C;
    public const byte CmdSelfUpdateSig = 0x0D;
    public const byte CmdAbort = 0x0E;
    public const byte CmdGetDeviceId = 0x0F;
    public const byte CmdGetDiag = 0x10;
    public const byte CmdReadFlash = 0x11;
    public const byte CmdCrcSectors = 0x12;
    public const byte CmdEnumerate = 0x13;

    // Protocol status codes
    public const byte StatusOk = 0x00;
    public const byte StatusErrGeneric = 0x01;
    public const byte StatusWrongState = 0x02;
    public const byte StatusBadAddr = 0x03;
    public const byte StatusBadLen = 0x04;
    public const byte StatusFlashErr = 0x05;
    public const byte StatusCrcMismatch = 0x06;
    public const byte StatusNotImpl = 0x07;
    public const byte StatusBadSeq = 0x08;
    public const byte StatusBadImage = 0x09;
    public const byte StatusAuthRequired = 0x0A;
    public const byte StatusAuthFail = 0x0B;
    public const byte StatusEraseDone = 0x80;
    public const byte StatusEraseProgress = 0x85;
    public const byte StatusPageOk = 0x81;
    public const byte StatusDataDone = 0x82;
    public const byte StatusSelfUpdateOk = 0x84;
    public const byte StatusResumeOk = 0x86;

    // Flash layout
    public const uint AppBase = 0x10008000;
    public const uint BankABase = AppBase;        // 0x10008000
    public const uint BankBBase = 0x10208000;
    public const byte CfgParamActiveBank = 0x11;
    public const int AppHeaderSize = 256;
    public const int FlashPageSize = 256;
    public const int FlashSectorSize = 4096;
    public const int BlCodeSize = 28 * 1024;

    // Reset modes
    public const byte ResetModeApp = 0x00;
    public const byte ResetModeBootloader = 0x01;

    public static string FormatStatus(byte status) => status switch
    {
        StatusOk => "OK (0x00)",
        StatusErrGeneric => "ERR_GENERIC (0x01)",
        StatusWrongState => "WRONG_STATE (0x02)",
        StatusBadAddr => "BAD_ADDR (0x03)",
        StatusBadLen => "BAD_LEN (0x04)",
        StatusFlashErr => "FLASH_ERR (0x05)",
        StatusCrcMismatch => "CRC_MISMATCH (0x06)",
        StatusNotImpl => "NOT_IMPL (0x07)",
        StatusBadSeq => "BAD_SEQ (0x08)",
        StatusBadImage => "BAD_IMAGE (0x09)",
        StatusAuthRequired => "AUTH_REQUIRED (0x0A)",
        StatusAuthFail => "AUTH_FAIL (0x0B)",
        StatusEraseDone => "ERASE_DONE (0x80)",
        StatusPageOk => "PAGE_OK (0x81)",
        StatusDataDone => "DATA_DONE (0x82)",
        StatusSelfUpdateOk => "SELF_UPDATE_OK (0x84)",
        StatusEraseProgress => "ERASE_PROGRESS (0x85)",
        StatusResumeOk => "RESUME_OK (0x86)",
        _ => $"UNKNOWN (0x{status:X2})"
    };

    public static string? GetHint(byte status) => status switch
    {
        StatusBadAddr => "Verify address is sector-aligned (4096 bytes) and within flash range",
        StatusBadLen => "Check firmware size -- must be > 0 and fit within the target flash region",
        StatusWrongState => "Try disconnecting and reconnecting. Expected flow: CONNECT -> ERASE -> DATA -> VERIFY",
        StatusAuthRequired => "Device requires authentication. Load the auth key file before flashing",
        StatusAuthFail => "Wrong key. Verify the key file matches the provisioned device key",
        StatusCrcMismatch => "Data corruption during transfer. Retry the flash operation",
        StatusFlashErr => "Flash hardware error. Try power-cycling the device. If persistent, the flash chip may be damaged",
        StatusBadImage => "Invalid binary -- bad vector table (SP or reset handler out of range)",
        StatusBadSeq => "Sequence error during data transfer. Abort and retry from the beginning",
        StatusNotImpl => "Command not supported by this bootloader version. Update the bootloader firmware",
        StatusErrGeneric => "Internal bootloader error. Try power-cycling the device",
        _ => null
    };

    public static string FormatResetReason(byte reason) => reason switch
    {
        0 => "Power-on",
        1 => "Brown-out",
        2 => "RUN pin",
        3 => "Watchdog",
        4 => "Debugger",
        5 => "Glitch",
        6 => "Core powerdown",
        7 => "Unknown",
        _ => $"0x{reason:X2}"
    };
}
