using System;

namespace CanBus;

public class CanTraceFrame
{
    public DateTime Timestamp { get; set; }
    public bool IsTx { get; set; }
    public uint CanId { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public byte Dlc => (byte)Data.Length;

    public string DirectionStr => IsTx ? "TX" : "RX";
    public string IdHex => $"0x{CanId:X3}";
    public string DataHex => Data.Length > 0 ? BitConverter.ToString(Data).Replace("-", " ") : "";

    public string Description => Describe(CanId, Data, IsTx);

    private static readonly string[] CmdNames =
    {
        "", "CONNECT", "ERASE", "DATA", "VERIFY", "RESET",
        "CONFIG_READ", "CONFIG_WRITE", "DEBUG_TOGGLE", "SELF_UPDATE",
        "GET_STATUS", "AUTH", "PROVISION_KEY", "SELF_UPDATE_SIG",
        "ABORT", "GET_DEVICE_ID", "GET_DIAG"
    };

    private static readonly string[] StateNames =
        { "IDLE", "AUTH_PENDING", "CONNECTED", "ERASING", "RECEIVING", "COMPLETE" };

    private static string CmdName(byte cmd) =>
        cmd < CmdNames.Length && CmdNames[cmd].Length > 0 ? CmdNames[cmd] : $"CMD_0x{cmd:X2}";

    private static string StatusName(byte st) => st switch
    {
        0x00 => "OK",
        0x01 => "ERR_GENERIC",
        0x02 => "WRONG_STATE",
        0x03 => "BAD_ADDR",
        0x04 => "BAD_LEN",
        0x05 => "FLASH_ERR",
        0x06 => "CRC_MISMATCH",
        0x08 => "BAD_SEQ",
        0x09 => "BAD_IMAGE",
        0x0A => "AUTH_REQUIRED",
        0x0B => "AUTH_FAIL",
        0x80 => "ERASE_DONE",
        0x81 => "PAGE_OK",
        0x82 => "DATA_DONE",
        0x84 => "SELF_UPDATE_OK",
        0x85 => "ERASE_PROGRESS",
        0x86 => "RESUME_OK",
        _ => $"0x{st:X2}"
    };

    private static string Describe(uint canId, byte[] data, bool isTx)
    {
        if (data.Length == 0) return "";

        // Debug frame (0x7FF or node-offset equivalent)
        if ((canId & 0x7FF) == 0x7FF)
            return "DEBUG";

        byte cmd = data[0];

        // TX command frame (cmd ID: base + 0)
        if (isTx && (canId & 0x7FC) == 0x700 && (canId & 3) == 0)
        {
            return CmdName(cmd);
        }

        // RX data frame (data ID: base + 2)
        if (!isTx && (canId & 0x7FC) == 0x700 && (canId & 3) == 2)
            return $"DATA seq={data[0]}";

        // RX response frame (resp ID: base + 1)
        if (!isTx && (canId & 0x7FC) == 0x700 && (canId & 3) == 1)
        {
            // Heartbeat: 4 bytes, no cmd echo — [major, minor, patch, button]
            // Distinguish from a command response: heartbeat has DLC=4 and
            // the CONNECT response (cmd=0x01) always has DLC >= 6.
            if (data.Length == 4 && cmd <= 0x0F)
            {
                // A real CONNECT response has DLC >= 6. DLC=4 with small first
                // byte is a heartbeat. Also, no other command response is exactly
                // 4 bytes with data[1] being a small version-like number.
                bool looksLikeHeartbeat = cmd <= 5; // major version 0-5
                if (looksLikeHeartbeat)
                    return $"HEARTBEAT v{data[0]}.{data[1]}.{data[2]}";
            }

            if (data.Length < 2) return CmdName(cmd);

            // Per-command response format decoding
            return cmd switch
            {
                // CONNECT: [cmd, status, major, minor, patch, proto_ver, ...]
                0x01 => data[1] == 0x00 && data.Length >= 5
                    ? $"CONNECT OK v{data[2]}.{data[3]}.{data[4]}"
                    : $"CONNECT {StatusName(data[1])}",

                // GET_STATUS: [cmd, state, lastError, bytesRx(4), resetReason]
                0x0A => data.Length >= 3
                    ? $"GET_STATUS state={StateName(data[1])} err={StatusName(data[2])}"
                    : "GET_STATUS",

                // CONFIG_READ: [cmd, param_id, status, value...]
                0x06 => data.Length >= 3
                    ? $"CONFIG_READ param=0x{data[1]:X2} {StatusName(data[2])}"
                    : "CONFIG_READ",

                // GET_DEVICE_ID: [cmd, frame_idx, data...]
                0x0F => $"GET_DEVICE_ID frame={data[1]}",

                // GET_DIAG: [cmd, parseErr(2), rxOvf(2), txRetry(2)]
                0x10 => data.Length >= 7
                    ? $"parse={data[1] | (data[2] << 8)} rxOvf={data[3] | (data[4] << 8)} txRetry={data[5] | (data[6] << 8)}"
                    : "",

                // CONNECT extension frames: [cmd, ext_idx, ...]
                // (already handled above for cmd=0x01 with data[1] != 0x00)

                // Standard status responses: [cmd, status, ...]
                // ERASE, DATA, VERIFY, RESET, CONFIG_WRITE, DEBUG_TOGGLE,
                // SELF_UPDATE, AUTH, PROVISION_KEY, SELF_UPDATE_SIG, ABORT
                _ => $"{CmdName(cmd)} {StatusName(data[1])}"
            };
        }

        return "";
    }

    private static string StateName(byte state) =>
        state < StateNames.Length ? StateNames[state] : $"0x{state:X2}";
}
