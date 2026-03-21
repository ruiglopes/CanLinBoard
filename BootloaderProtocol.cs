using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using CanBus;
using static CanBus.ProtocolConstants;

namespace CanBus.Protocol;

public class BootloaderProtocol : IProtocolTransport
{
    public uint CmdCanId { get; set; } = CanIdCmdDefault;
    public uint RspCanId { get; set; } = CanIdRespDefault;
    public uint DataCanId { get; set; } = CanIdDataDefault;

    // PAGE_OK retry configuration
    private const int PageOkMaxRetries = 3;
    private const int GetStatusTimeoutMs = 2000;

    // Semantic timeout constants (base values, scaled by TimeoutMultiplier)
    private const int PageOkTimeoutMs = 5000;
    private const int DataDoneTimeoutMs = 5000;
    private const int VerifyTimeoutMs = 5000;
    private const int SelfUpdateTimeoutMs = 5000;

    // CMD_RESET unlock key (0xB007CAFE little-endian)
    private static readonly byte[] ResetUnlockKey = { 0xFE, 0xCA, 0x07, 0xB0 };

    private readonly ICanAdapter _pcan;

    /// <summary>
    /// Timeout multiplier applied to all protocol timeouts.
    /// 1.0 = default (native adapters), higher values for slow links (e.g. SLCAN).
    /// Set before Connect(), or leave at 1.0 for auto-detection.
    /// </summary>
    public double TimeoutMultiplier { get; set; } = 1.0;

    /// <summary>
    /// True if TimeoutMultiplier was explicitly set by the user (not auto-detected).
    /// </summary>
    public bool IsManualMultiplier { get; set; }

    /// <summary>
    /// Round-trip time measured during the last Connect() call, in milliseconds.
    /// </summary>
    public int MeasuredRttMs { get; private set; }

    public BootloaderProtocol(ICanAdapter adapter)
    {
        _pcan = adapter;
    }

    private int Scaled(int baseMs) => (int)(baseMs * TimeoutMultiplier);

    public BootloaderInfo Connect(CancellationToken ct = default)
    {
        _pcan.Drain();
        long sendTick = Environment.TickCount64;
        _pcan.Send(CmdCanId, new byte[] { CmdConnect, 0x03 });

        var (resp, info, rttMs) = WaitForConnectResponse(sendTick, ct);
        if (resp == null)
            throw new TimeoutException("No CONNECT response. Is the board in bootloader mode?");

        if (resp[1] != StatusOk)
            throw new ProtocolException($"CONNECT rejected: {FormatStatus(resp[1])}", resp[1]);

        // Record measured RTT and auto-detect multiplier if not manually set
        MeasuredRttMs = rttMs;
        if (!IsManualMultiplier)
        {
            TimeoutMultiplier = rttMs switch
            {
                < 20 => 1.0,
                < 50 => 1.5,
                < 150 => 2.0,
                _ => 3.0
            };
        }

        ParseConnectExtensions(info, ct);

        // Retry once if version is 0.0.0 (malformed frame)
        if (!info.IsVersionValid)
        {
            _pcan.Drain(200);
            sendTick = Environment.TickCount64;
            _pcan.Send(CmdCanId, new byte[] { CmdConnect, 0x03 });

            var (resp2, info2, _) = WaitForConnectResponse(sendTick, ct);
            if (resp2 != null && resp2[1] == StatusOk)
            {
                info = info2;
                ParseConnectExtensions(info, ct);
            }
        }

        return info;
    }

    /// <summary>
    /// Connect to a specific device by its 6-byte UID.
    /// Sends CMD_CONNECT with sub-command 0x03 and UID filter on default CAN IDs.
    /// Only the device with a matching UID will respond.
    /// </summary>
    public BootloaderInfo ConnectByUid(byte[] uid, CancellationToken ct = default)
    {
        if (uid == null || uid.Length != 6)
            throw new ArgumentException("UID must be exactly 6 bytes");

        ResetCanIds();

        var payload = new byte[8];
        payload[0] = CmdConnect;
        payload[1] = 0x03;
        Array.Copy(uid, 0, payload, 2, 6);

        _pcan.Drain(50);
        long sendTick = Environment.TickCount64;
        _pcan.Send(CmdCanId, payload);

        var (resp, info, rttMs) = WaitForConnectResponse(sendTick, ct);
        if (resp == null)
            throw new TimeoutException("No CONNECT response from targeted device (UID may not be on bus)");
        if (resp[1] != StatusOk)
            throw new ProtocolException($"CONNECT rejected: {FormatStatus(resp[1])}", resp[1]);

        MeasuredRttMs = rttMs;
        if (!IsManualMultiplier)
        {
            TimeoutMultiplier = rttMs switch
            {
                < 20 => 1.0,
                < 50 => 1.5,
                < 150 => 2.0,
                _ => 3.0
            };
        }

        ParseConnectExtensions(info, ct);
        return info;
    }

    private (byte[]? resp, BootloaderInfo info, int rttMs) WaitForConnectResponse(long sendTick, CancellationToken ct = default)
    {
        var info = new BootloaderInfo();
        var deadline = Environment.TickCount64 + Scaled(3000);
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            int remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
            var result = _pcan.Receive(Math.Min(remaining, 100), RspCanId);
            if (result == null) continue;

            var data = result.Value.Data;
            if (data.Length >= 6 && data[0] == CmdConnect)
            {
                int rtt = (int)(Environment.TickCount64 - sendTick);
                info.VersionMajor = data[2];
                info.VersionMinor = data[3];
                info.VersionPatch = data[4];
                info.ProtoVersion = data[5];
                if (data.Length >= 8)
                    info.PageSize = data[6] | (data[7] << 8);
                return (data, info, rtt);
            }
        }
        return (null, info, 0);
    }

    private void ParseConnectExtensions(BootloaderInfo info, CancellationToken ct = default)
    {
        // Extension frame 1 (proto >= 2): debug_can_id + nonce[0..3]
        if (info.ProtoVersion >= 2)
        {
            ct.ThrowIfCancellationRequested();
            var ext = _pcan.Receive(Scaled(500), RspCanId);
            if (ext != null && ext.Value.Data.Length >= 4 && ext.Value.Data[0] == CmdConnect && ext.Value.Data[1] == 0x01)
            {
                info.DebugCanId = (uint)(ext.Value.Data[2] | (ext.Value.Data[3] << 8));
                if (ext.Value.Data.Length >= 8)
                {
                    info.Nonce = new byte[8];
                    info.Nonce[0] = ext.Value.Data[4];
                    info.Nonce[1] = ext.Value.Data[5];
                    info.Nonce[2] = ext.Value.Data[6];
                    info.Nonce[3] = ext.Value.Data[7];
                }
            }
        }

        // Extension frame 2 (proto >= 3): nonce[4..7] + auth_required
        if (info.ProtoVersion >= 3)
        {
            ct.ThrowIfCancellationRequested();
            var ext2 = _pcan.Receive(Scaled(500), RspCanId);
            if (ext2 != null && ext2.Value.Data.Length >= 7 && ext2.Value.Data[0] == CmdConnect && ext2.Value.Data[1] == 0x02)
            {
                if (info.Nonce != null)
                {
                    info.Nonce[4] = ext2.Value.Data[2];
                    info.Nonce[5] = ext2.Value.Data[3];
                    info.Nonce[6] = ext2.Value.Data[4];
                    info.Nonce[7] = ext2.Value.Data[5];
                }
                info.AuthRequired = ext2.Value.Data[6] != 0;
            }
        }
    }

    public int Erase(uint address, uint size, Action<string>? log = null, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        uint eraseSize = ((size + FlashSectorSize - 1) / FlashSectorSize) * FlashSectorSize;
        int expectedSectors = (int)(eraseSize / FlashSectorSize);

        _pcan.Drain();

        var cmd = new byte[8];
        cmd[0] = CmdErase;
        WriteUInt32LE(cmd, 1, address);
        WriteUInt24LE(cmd, 5, eraseSize);
        _pcan.Send(CmdCanId, cmd);

        // Wait for ACK
        var ack = ReceiveResponse(CmdErase, Scaled(2000), ct);
        if (ack == null || ack.Length < 2 || ack[1] != StatusOk)
        {
            byte errStatus = ack?.Length >= 2 ? ack[1] : (byte)0xFF;
            throw new ProtocolException($"ERASE ACK failed: {FormatResp(ack)}", errStatus);
        }

        // Wait for ERASE_DONE, collecting ERASE_PROGRESS along the way
        int timeout = Scaled(Math.Max(10000, expectedSectors * 100));
        while (true)
        {
            var resp = ReceiveResponse(CmdErase, timeout, ct);
            if (resp == null)
                throw new ProtocolException("ERASE timed out waiting for ERASE_DONE");

            if (resp.Length >= 2 && resp[1] == StatusEraseProgress)
            {
                int done = resp.Length >= 4 ? (resp[2] | (resp[3] << 8)) : 0;
                int total = resp.Length >= 6 ? (resp[4] | (resp[5] << 8)) : expectedSectors;
                progress?.Invoke(done, total);
                continue;
            }

            if (resp.Length < 2 || resp[1] != StatusEraseDone)
            {
                byte errStatus = resp.Length >= 2 ? resp[1] : (byte)0xFF;
                throw new ProtocolException($"ERASE_DONE failed: {FormatResp(resp)}", errStatus);
            }

            int sectors = resp.Length >= 4 ? (resp[2] | (resp[3] << 8)) : 0;
            log?.Invoke($"Erased {sectors} sector(s)");
            return sectors;
        }
    }

    public void SendData(uint address, byte[] firmware, Action<int, int>? progress = null, Action<string>? log = null, CancellationToken ct = default)
    {
        int totalSize = firmware.Length;
        int expectedPages = (totalSize + FlashPageSize - 1) / FlashPageSize;

        _pcan.Drain();

        // CMD_DATA start frame
        var cmd = new byte[8];
        cmd[0] = CmdData;
        WriteUInt32LE(cmd, 1, address);
        WriteUInt24LE(cmd, 5, (uint)totalSize);
        _pcan.Send(CmdCanId, cmd);

        var ack = ReceiveResponse(CmdData, Scaled(2000), ct);
        if (ack == null || ack.Length < 2 || ack[1] != StatusOk)
        {
            byte errStatus = ack?.Length >= 2 ? ack[1] : (byte)0xFF;
            throw new ProtocolException($"DATA ACK failed: {FormatResp(ack)}", errStatus);
        }

        // Send data frames with PAGE_OK flow control:
        // Send one page worth of frames, wait for PAGE_OK, repeat.
        // On PAGE_OK timeout, use CMD_GET_STATUS to check if page was written.
        int offset = 0;
        int seq = 0;
        int frameCount = 0;
        int pagesAcked = 0;
        int nextPageBoundary = FlashPageSize;
        bool dataDoneReceived = false;

        while (offset < totalSize)
        {
            ct.ThrowIfCancellationRequested();
            int chunk = Math.Min(7, totalSize - offset);
            var frame = new byte[1 + chunk];
            frame[0] = (byte)(seq & 0xFF);
            Array.Copy(firmware, offset, frame, 1, chunk);
            _pcan.Send(DataCanId, frame);

            offset += chunk;
            seq++;
            frameCount++;

            // Wait for PAGE_OK after each page boundary (or end of data)
            if (offset >= nextPageBoundary || offset >= totalSize)
            {
                bool isLastPage = offset >= totalSize;
                uint expectedBytes = (uint)Math.Min(nextPageBoundary, totalSize);
                bool pageConfirmed = false;

                for (int retry = 0; retry < PageOkMaxRetries; retry++)
                {
                    var pageResp = ReceiveResponse(CmdData, Scaled(PageOkTimeoutMs), ct);

                    if (pageResp != null)
                    {
                        if (pageResp[1] == StatusPageOk)
                        {
                            pageConfirmed = true;
                            break;
                        }
                        if (pageResp[1] == StatusDataDone && isLastPage)
                        {
                            dataDoneReceived = true;
                            pageConfirmed = true;
                            break;
                        }
                        throw new ProtocolException($"Data transfer error at page {pagesAcked + 1}: {FormatStatus(pageResp[1])}", pageResp[1]);
                    }

                    // PAGE_OK timeout — query bootloader via GET_STATUS
                    log?.Invoke($"PAGE_OK timeout for page {pagesAcked + 1}/{expectedPages}, querying status (retry {retry + 1}/{PageOkMaxRetries})...");
                    var bytesRcvd = QueryBytesReceived(ct);
                    if (bytesRcvd != null)
                    {
                        if (bytesRcvd.Value >= expectedBytes)
                        {
                            log?.Invoke($"Bootloader confirmed {bytesRcvd.Value} bytes received — continuing");
                            // Drain any stale PAGE_OK that may arrive late
                            ReceiveResponse(CmdData, 100);
                            pageConfirmed = true;
                            break;
                        }
                        // Bootloader has fewer bytes than expected — data frames were lost
                        throw new ProtocolException($"Data frame loss at page {pagesAcked + 1}: bootloader has {bytesRcvd.Value} bytes, expected {expectedBytes}");
                    }
                    // GET_STATUS itself timed out — retry
                }

                if (!pageConfirmed)
                    throw new ProtocolException($"No PAGE_OK for page {pagesAcked + 1}/{expectedPages} after {PageOkMaxRetries} attempts");

                pagesAcked++;
                nextPageBoundary += FlashPageSize;
                progress?.Invoke(offset, totalSize);
            }
        }

        // Wait for DATA_DONE (unless already received in page loop)
        if (!dataDoneReceived)
        {
            var done = ReceiveResponse(CmdData, Scaled(DataDoneTimeoutMs), ct);
            if (done == null || done.Length < 2 || done[1] != StatusDataDone)
            {
                // Fallback: use GET_STATUS to confirm transfer completed
                var bytesRcvd = QueryBytesReceived(ct);
                if (bytesRcvd == null || bytesRcvd.Value != (uint)totalSize)
                    throw new ProtocolException($"No DATA_DONE received: {FormatResp(done)}");
                log?.Invoke($"DATA_DONE missed but bootloader confirmed {bytesRcvd.Value} bytes received");
            }
            else
            {
                uint totalReceived = done.Length >= 6 ? ReadUInt32LE(done, 2) : 0;
                if (totalReceived != (uint)totalSize)
                    throw new ProtocolException($"DATA_DONE bytes={totalReceived}, expected {totalSize}");
            }
        }

        log?.Invoke($"Sent {frameCount} frames, {pagesAcked} pages written, {totalSize} bytes confirmed");
    }

    /// <summary>
    /// Query the bootloader for the resume offset after a failed data transfer.
    /// Returns the page-aligned byte offset, or null if resume is not possible.
    /// </summary>
    public uint? QueryResumeOffset(CancellationToken ct = default)
    {
        var (_, _, bytesReceived, _) = GetStatus();
        if (bytesReceived == 0)
            return null;
        uint pageAligned = (bytesReceived / FlashPageSize) * FlashPageSize;
        return pageAligned > 0 ? pageAligned : null;
    }

    /// <summary>
    /// Resume a data transfer from a given offset. Sets bit 23 in the size field.
    /// Accepts StatusResumeOk as the ACK.
    /// </summary>
    public void SendDataResume(uint baseAddress, byte[] firmware, int resumeOffset,
        Action<int, int>? progress = null, Action<string>? log = null, CancellationToken ct = default)
    {
        if (resumeOffset < 0)
            throw new ArgumentException($"Resume offset must be non-negative: {resumeOffset}", nameof(resumeOffset));
        if (resumeOffset >= firmware.Length)
            throw new ArgumentException($"Resume offset {resumeOffset} exceeds firmware size {firmware.Length}", nameof(resumeOffset));
        if (resumeOffset % FlashPageSize != 0)
            throw new ArgumentException($"Resume offset {resumeOffset} is not page-aligned (must be multiple of {FlashPageSize})", nameof(resumeOffset));

        int remainingSize = firmware.Length - resumeOffset;
        uint resumeAddr = baseAddress + (uint)resumeOffset;
        int expectedPages = (remainingSize + FlashPageSize - 1) / FlashPageSize;

        _pcan.Drain();

        // CMD_DATA with resume flag (bit 23)
        var cmd = new byte[8];
        cmd[0] = CmdData;
        WriteUInt32LE(cmd, 1, resumeAddr);
        WriteUInt24LE(cmd, 5, (uint)remainingSize | 0x800000);
        _pcan.Send(CmdCanId, cmd);

        var ack = ReceiveResponse(CmdData, Scaled(2000), ct);
        if (ack == null || ack.Length < 2 || ack[1] != StatusResumeOk)
        {
            byte errStatus = ack?.Length >= 2 ? ack[1] : (byte)0xFF;
            throw new ProtocolException($"RESUME ACK failed: {FormatResp(ack)}", errStatus);
        }

        log?.Invoke($"Resume from offset {resumeOffset}, {remainingSize} bytes remaining");

        // Send data frames with PAGE_OK flow control
        int offset = resumeOffset;
        int seq = 0;
        int frameCount = 0;
        int pagesAcked = 0;
        int nextPageBoundary = FlashPageSize;
        int bytesSent = 0;
        bool dataDoneReceived = false;

        while (bytesSent < remainingSize)
        {
            ct.ThrowIfCancellationRequested();
            int chunk = Math.Min(7, remainingSize - bytesSent);
            var frame = new byte[1 + chunk];
            frame[0] = (byte)(seq & 0xFF);
            Array.Copy(firmware, offset, frame, 1, chunk);
            _pcan.Send(DataCanId, frame);

            offset += chunk;
            bytesSent += chunk;
            seq++;
            frameCount++;

            if (bytesSent >= nextPageBoundary || bytesSent >= remainingSize)
            {
                bool isLastPage = bytesSent >= remainingSize;
                uint expectedBytes = (uint)Math.Min(nextPageBoundary, remainingSize);
                bool pageConfirmed = false;

                for (int retry = 0; retry < PageOkMaxRetries; retry++)
                {
                    var pageResp = ReceiveResponse(CmdData, Scaled(PageOkTimeoutMs), ct);

                    if (pageResp != null)
                    {
                        if (pageResp[1] == StatusPageOk)
                        {
                            pageConfirmed = true;
                            break;
                        }
                        if (pageResp[1] == StatusDataDone && isLastPage)
                        {
                            dataDoneReceived = true;
                            pageConfirmed = true;
                            break;
                        }
                        throw new ProtocolException($"Resume transfer error at page {pagesAcked + 1}: {FormatStatus(pageResp[1])}", pageResp[1]);
                    }

                    log?.Invoke($"PAGE_OK timeout for page {pagesAcked + 1}/{expectedPages}, retry {retry + 1}/{PageOkMaxRetries}");
                    var bytesRcvd = QueryBytesReceived(ct);
                    if (bytesRcvd != null)
                    {
                        if (bytesRcvd.Value >= expectedBytes)
                        {
                            ReceiveResponse(CmdData, 100);
                            pageConfirmed = true;
                            break;
                        }
                        throw new ProtocolException($"Data frame loss at page {pagesAcked + 1}: bootloader has {bytesRcvd.Value} bytes, expected {expectedBytes}");
                    }
                }

                if (!pageConfirmed)
                    throw new ProtocolException($"No PAGE_OK for page {pagesAcked + 1}/{expectedPages} after {PageOkMaxRetries} attempts");

                pagesAcked++;
                nextPageBoundary += FlashPageSize;
                progress?.Invoke(resumeOffset + bytesSent, firmware.Length);
            }
        }

        if (!dataDoneReceived)
        {
            var done = ReceiveResponse(CmdData, Scaled(DataDoneTimeoutMs), ct);
            if (done == null || done.Length < 2 || done[1] != StatusDataDone)
            {
                var bytesRcvd = QueryBytesReceived(ct);
                if (bytesRcvd == null || bytesRcvd.Value != (uint)remainingSize)
                    throw new ProtocolException($"No DATA_DONE received after resume: {FormatResp(done)}");
            }
            else
            {
                uint totalReceived = done.Length >= 6 ? ReadUInt32LE(done, 2) : 0;
                if (totalReceived != (uint)remainingSize)
                    throw new ProtocolException($"DATA_DONE bytes={totalReceived}, expected {remainingSize}");
            }
        }

        log?.Invoke($"Resume complete: {frameCount} frames, {pagesAcked} pages, {remainingSize} bytes");
    }

    public (bool Match, uint DeviceCrc) Verify(uint address, byte[] firmware, CancellationToken ct = default)
    {
        int totalSize = firmware.Length;
        uint expectedCrc = ComputeCrc32(firmware);

        _pcan.Drain();

        var cmd = new byte[8];
        cmd[0] = CmdVerify;
        WriteUInt32LE(cmd, 1, address);
        WriteUInt24LE(cmd, 5, (uint)totalSize);
        _pcan.Send(CmdCanId, cmd);

        var resp = ReceiveResponse(CmdVerify, Scaled(VerifyTimeoutMs), ct);
        if (resp == null)
            throw new TimeoutException("No VERIFY response");
        if (resp.Length < 2)
            throw new ProtocolException($"VERIFY response too short: {resp.Length} bytes");

        uint deviceCrc = resp.Length >= 6 ? ReadUInt32LE(resp, 2) : 0;

        if (resp[1] != StatusOk)
            return (false, deviceCrc);

        return (deviceCrc == expectedCrc, deviceCrc);
    }

    /// <summary>
    /// Full application validation (header magic + CRC32 + HMAC).
    /// Sends CMD_VERIFY with addr=0, size=0 to trigger the bootloader's
    /// full app validation path.
    /// </summary>
    public (bool Pass, uint DeviceCrc) VerifyApp(CancellationToken ct = default)
    {
        _pcan.Drain();
        var cmd = new byte[8];
        cmd[0] = CmdVerify;
        _pcan.Send(CmdCanId, cmd);

        var resp = ReceiveResponse(CmdVerify, Scaled(VerifyTimeoutMs), ct);
        if (resp == null)
            throw new TimeoutException("No VERIFY response");
        if (resp.Length < 2)
            throw new ProtocolException($"VERIFY response too short: {resp.Length} bytes");

        uint deviceCrc = resp.Length >= 6 ? ReadUInt32LE(resp, 2) : 0;
        bool pass = resp[1] == StatusOk;
        return (pass, deviceCrc);
    }

    public byte[]? Reset(byte mode, CancellationToken ct = default)
    {
        _pcan.Drain();
        var cmd = new byte[2 + ResetUnlockKey.Length];
        cmd[0] = CmdReset;
        cmd[1] = mode;
        Array.Copy(ResetUnlockKey, 0, cmd, 2, ResetUnlockKey.Length);
        _pcan.Send(CmdCanId, cmd);

        // ACK is best-effort — board may reset before sending it
        return ReceiveResponse(CmdReset, Scaled(1000), ct);
    }

    public (ConfigStatus Status, byte[] Value) ConfigRead(byte paramId)
    {
        _pcan.Drain();
        _pcan.Send(CmdCanId, new[] { CmdConfigRead, paramId });

        var resp = ReceiveResponse(CmdConfigRead, Scaled(2000));
        if (resp == null)
            throw new TimeoutException("No CONFIG_READ response");

        // Response: [cmd, param_id, status, value_bytes...]
        if (resp.Length < 3)
            throw new ProtocolException($"CONFIG_READ response too short: {resp.Length} bytes");

        var status = (ConfigStatus)resp[2];
        var value = new byte[resp.Length - 3];
        if (value.Length > 0)
            Array.Copy(resp, 3, value, 0, value.Length);

        return (status, value);
    }

    public ConfigStatus ConfigWrite(byte paramId, byte[] value)
    {
        _pcan.Drain();

        var cmd = new byte[2 + value.Length];
        cmd[0] = CmdConfigWrite;
        cmd[1] = paramId;
        Array.Copy(value, 0, cmd, 2, value.Length);
        _pcan.Send(CmdCanId, cmd);

        var resp = ReceiveResponse(CmdConfigWrite, Scaled(2000));
        if (resp == null)
            throw new TimeoutException("No CONFIG_WRITE response");

        // Response: [cmd, param_id, status]
        if (resp.Length < 3)
            throw new ProtocolException($"CONFIG_WRITE response too short: {resp.Length} bytes");

        return (ConfigStatus)resp[2];
    }

    /// <summary>Switch the active bank (0=A, 1=B). Device validates target bank.</summary>
    public ConfigStatus SwitchBank(byte bank)
    {
        return ConfigWrite(ProtocolConstants.CfgParamActiveBank, new[] { bank });
    }

    public void Authenticate(byte[] nonce, byte[] key, CancellationToken ct = default)
    {
        _pcan.Drain();

        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(nonce);

        // CMD_AUTH: [0x0B, T0, T1, T2, T3, T4, T5, T6] — 7-byte truncated HMAC
        var cmd = new byte[8];
        cmd[0] = CmdAuth;
        Array.Copy(hash, 0, cmd, 1, 7);
        _pcan.Send(CmdCanId, cmd);

        var resp = ReceiveResponse(CmdAuth, Scaled(3000), ct);
        if (resp == null)
            throw new TimeoutException("No AUTH response");
        if (resp.Length < 2 || resp[1] != StatusOk)
        {
            byte status = resp.Length >= 2 ? resp[1] : (byte)0xFF;
            string detail = status switch
            {
                StatusAuthFail => "AUTH_FAIL (0x0B) — wrong key?",
                StatusWrongState => "WRONG_STATE (0x02) — not AUTH_PENDING",
                _ => FormatStatus(status)
            };
            throw new ProtocolException($"Authentication failed: {detail}", status);
        }
    }

    public (byte State, byte LastError, uint BytesReceived, byte ResetReason) GetStatus()
    {
        _pcan.Drain();
        _pcan.Send(CmdCanId, new byte[] { CmdGetStatus });

        var resp = ReceiveResponse(CmdGetStatus, Scaled(2000));
        if (resp == null)
            throw new TimeoutException("No GET_STATUS response");
        if (resp.Length < 7)
            throw new ProtocolException($"GET_STATUS response too short: {resp.Length} bytes");

        byte state = resp[1];
        byte lastError = resp[2];
        uint bytesReceived = ReadUInt32LE(resp, 3);
        byte resetReason = resp.Length >= 8 ? resp[7] : (byte)0xFF;

        return (state, lastError, bytesReceived, resetReason);
    }

    /// <summary>
    /// Query CAN bus diagnostic counters. Returns null if firmware doesn't support this command.
    /// </summary>
    public (ushort ParseErrors, ushort RxOverflows, ushort TxRetries)? GetDiagnostics()
    {
        _pcan.Drain();
        _pcan.Send(CmdCanId, new byte[] { CmdGetDiag });

        var resp = ReceiveResponse(CmdGetDiag, Scaled(2000));
        if (resp == null)
            return null;  // Firmware doesn't support CMD_GET_DIAG
        if (resp.Length < 7)
            return null;

        ushort parseErrors = ReadUInt16LE(resp, 1);
        ushort rxOverflows = ReadUInt16LE(resp, 3);
        ushort txRetries   = ReadUInt16LE(resp, 5);

        return (parseErrors, rxOverflows, txRetries);
    }

    /// <summary>
    /// Read flash memory (restricted to config sector and app header).
    /// </summary>
    /// <param name="offset">24-bit offset from FLASH_BASE (0x10000000).</param>
    /// <param name="length">Bytes to read (1-64).</param>
    /// <returns>Read data bytes, or null on timeout.</returns>
    public byte[]? ReadFlash(uint offset, byte length, CancellationToken ct = default)
    {
        if (length == 0 || length > 64)
            throw new ArgumentOutOfRangeException(nameof(length), "Must be 1-64");

        _pcan.Drain();
        var cmd = new byte[] {
            CmdReadFlash,
            (byte)((offset >> 16) & 0xFF),
            (byte)((offset >> 8) & 0xFF),
            (byte)(offset & 0xFF),
            length
        };
        _pcan.Send(CmdCanId, cmd);

        // Calculate expected frame count
        int frameCount = (length <= 3) ? 1 : 1 + ((length - 3 + 5) / 6);

        // Collect frames
        var data = new byte[length];
        int dataPos = 0;
        int framesReceived = 0;
        var deadline = Environment.TickCount64 + Scaled(3000);

        while (framesReceived < frameCount && Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            int remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
            var result = _pcan.Receive(Math.Min(remaining, 100), RspCanId);
            if (result == null) continue;

            var frame = result.Value.Data;
            if (frame.Length < 2 || frame[0] != CmdReadFlash)
                continue;

            if (framesReceived == 0)
            {
                // Frame 0: [cmd, status, addr_hi, addr_mid, addr_lo, data...]
                byte status = frame[1];
                if (status != StatusOk)
                    throw new ProtocolException(
                        $"READ_FLASH error: {FormatStatus(status)}");
                int n = Math.Min(frame.Length - 5, length - dataPos);
                if (n > 0)
                {
                    Array.Copy(frame, 5, data, dataPos, n);
                    dataPos += n;
                }
            }
            else
            {
                // Continuation: [cmd, seq, data...]
                int n = Math.Min(frame.Length - 2, length - dataPos);
                if (n > 0)
                {
                    Array.Copy(frame, 2, data, dataPos, n);
                    dataPos += n;
                }
            }
            framesReceived++;
        }

        return framesReceived > 0 ? data : null;
    }

    /// <summary>
    /// Read the 256-byte app header in 4 paged requests.
    /// </summary>
    public byte[]? ReadAppHeader(CancellationToken ct = default)
    {
        const uint appHeaderOffset = 0x8000;  // APP_BASE - FLASH_BASE
        var header = new byte[AppHeaderSize];
        for (int i = 0; i < 4; i++)
        {
            var chunk = ReadFlash(appHeaderOffset + (uint)(i * 64), 64, ct);
            if (chunk == null) return null;
            Array.Copy(chunk, 0, header, i * 64, 64);
        }
        return header;
    }

    /// <summary>
    /// Request per-sector CRC32 values for the installed app firmware.
    /// Returns null if no app installed or on timeout.
    /// Missing sectors (dropped frames) are set to null in the array.
    /// </summary>
    public uint?[]? GetSectorCrcs(uint? bankAddress = null, CancellationToken ct = default)
    {
        _pcan.Drain();
        if (bankAddress.HasValue)
        {
            var cmd = new byte[5];
            cmd[0] = CmdCrcSectors;
            WriteUInt32LE(cmd, 1, bankAddress.Value);
            _pcan.Send(CmdCanId, cmd);
        }
        else
        {
            _pcan.Send(CmdCanId, new byte[] { CmdCrcSectors });
        }

        // Frame 0: [cmd, status, count_lo, count_hi]
        var frame0 = ReceiveResponse(CmdCrcSectors, Scaled(3000), ct);
        if (frame0 == null || frame0.Length < 4)
            return null;
        if (frame0[1] != StatusOk)
            return null;

        int sectorCount = frame0[2] | (frame0[3] << 8);
        if (sectorCount == 0)
            return null;

        var crcs = new uint?[sectorCount];
        int received = 0;
        var deadline = Environment.TickCount64 + Scaled(sectorCount * 50 + 3000);

        while (received < sectorCount && Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            int remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
            var result = _pcan.Receive(Math.Min(remaining, 200), RspCanId);
            if (result == null) continue;

            var data = result.Value.Data;
            if (data.Length < 7 || data[0] != CmdCrcSectors)
                continue;

            int idx = data[1] | (data[2] << 8);
            if (idx < 0 || idx >= sectorCount)
                continue;

            if (crcs[idx] == null)
            {
                crcs[idx] = (uint)(data[3] | (data[4] << 8) | (data[5] << 16) | (data[6] << 24));
                received++;
            }
        }

        return crcs;
    }

    public void Abort()
    {
        _pcan.Drain();
        _pcan.Send(CmdCanId, new byte[] { CmdAbort });

        var resp = ReceiveResponse(CmdAbort, Scaled(2000));
        if (resp == null)
            throw new TimeoutException("No ABORT response");
        if (resp.Length < 2 || resp[1] != StatusOk)
        {
            byte status = resp.Length >= 2 ? resp[1] : (byte)0xFF;
            throw new ProtocolException($"ABORT rejected: {FormatStatus(status)}");
        }
    }

    public DeviceIdentity? GetDeviceId()
    {
        _pcan.Drain();
        _pcan.Send(CmdCanId, new byte[] { CmdGetDeviceId });

        // Collect up to 3 response frames, indexed by sequence byte (first-wins)
        var frames = new byte[]?[3];
        int count = 0;
        var deadline = Environment.TickCount64 + Scaled(3000);
        while (count < 3 && Environment.TickCount64 < deadline)
        {
            int remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
            var result = _pcan.Receive(Math.Min(remaining, 100), RspCanId);
            if (result == null) continue;

            var data = result.Value.Data;
            if (data.Length >= 2 && data[0] == CmdGetDeviceId)
            {
                int seq = data[1] switch
                {
                    StatusOk => 0,  // Frame 0: status=OK (0x00)
                    0x01 => 1,      // Frame 1: ext_seq=0x01
                    0x02 => 2,      // Frame 2: ext_seq=0x02
                    _ => -1         // Error or unknown — ignore
                };
                if (seq >= 0 && frames[seq] == null)
                {
                    frames[seq] = data;
                    count++;
                }
            }
        }

        // Frames 0 and 1 are required; frame 2 (CS1 JEDEC) is optional
        if (frames[0] == null || frames[1] == null) return null;

        var id = new DeviceIdentity();

        // Frame 0: [cmd, status, uid0..uid5]
        var f0 = frames[0]!;
        if (f0.Length >= 8 && f0[1] == StatusOk)
        {
            Array.Copy(f0, 2, id.UniqueId, 0, 6);
        }

        // Frame 1: [cmd, 0x01, uid6, uid7, cs0_mfr, cs0_type, cs0_cap, cs1_mfr]
        var f1 = frames[1]!;
        if (f1.Length >= 7)
        {
            id.UniqueId[6] = f1[2];
            id.UniqueId[7] = f1[3];
            id.FlashJedecCs0[0] = f1[4];
            id.FlashJedecCs0[1] = f1[5];
            id.FlashJedecCs0[2] = f1[6];
            if (f1.Length >= 8)
                id.FlashJedecCs1[0] = f1[7];
        }

        // Frame 2: [cmd, 0x02, cs1_type, cs1_cap] (optional)
        var f2 = frames[2];
        if (f2 != null && f2.Length >= 4)
        {
            id.FlashJedecCs1[1] = f2[2];
            id.FlashJedecCs1[2] = f2[3];
        }

        return id;
    }

    public void DebugToggle(bool enable, byte level = 2)
    {
        _pcan.Drain();
        _pcan.Send(CmdCanId, new[] { CmdDebugToggle, (byte)(enable ? 1 : 0), level });

        // Best-effort ACK
        ReceiveResponse(CmdDebugToggle, Scaled(1000));
    }

    /// <summary>
    /// Set CAN IDs for a specific node ID (0-15).
    /// </summary>
    public void SetNodeCanIds(byte nodeId)
    {
        CmdCanId = CanIdCmdDefault + nodeId * 4u;
        RspCanId = CmdCanId + 1;
        DataCanId = CmdCanId + 2;
        _pcan.ResponseCanId = RspCanId;
    }

    /// <summary>
    /// Reset CAN IDs to defaults (node 0).
    /// </summary>
    public void ResetCanIds()
    {
        CmdCanId = CanIdCmdDefault;
        RspCanId = CanIdRespDefault;
        DataCanId = CanIdDataDefault;
        _pcan.ResponseCanId = CanIdRespDefault;
    }

    /// <summary>
    /// Scan the CAN bus for bootloaders on node IDs 0-15.
    /// Returns a list of discovered devices.
    /// </summary>
    public List<DiscoveredDevice> ScanBus(Action<string>? log = null)
    {
        var devices = new List<DiscoveredDevice>();
        var savedCmd = CmdCanId;
        var savedRsp = RspCanId;
        var savedData = DataCanId;
        var savedMultiplier = TimeoutMultiplier;
        var savedManual = IsManualMultiplier;

        try
        {
            // Use fast, fixed timeouts for scanning
            TimeoutMultiplier = 1.0;
            IsManualMultiplier = true;

            for (byte nodeId = 0; nodeId <= 15; nodeId++)
            {
                SetNodeCanIds(nodeId);
                _pcan.Drain(50);

                try
                {
                    _pcan.Send(CmdCanId, new byte[] { CmdConnect, 0x03 });

                    // Short timeout for scan
                    var resp = ReceiveResponse(CmdConnect, 500);
                    if (resp != null && resp.Length >= 6 && resp[1] == StatusOk)
                    {
                        var info = new BootloaderInfo
                        {
                            VersionMajor = resp[2],
                            VersionMinor = resp[3],
                            VersionPatch = resp[4],
                            ProtoVersion = resp[5],
                        };

                        // Try to get device identity (best-effort)
                        if (FeatureRequirements.IsSupported(FeatureRequirements.DeviceIdentity, info))
                        {
                            try
                            {
                                var devId = GetDeviceId();
                                if (devId != null)
                                    info.DeviceIdentity = devId;
                            }
                            catch { }
                        }

                        var device = new DiscoveredDevice
                        {
                            NodeId = nodeId,
                            Info = info,
                        };
                        devices.Add(device);
                        log?.Invoke($"Found device at node {nodeId}: v{info.VersionString}");

                        // Send RESET to bootloader mode to release session
                        try
                        {
                            var resetCmd = new byte[2 + ResetUnlockKey.Length];
                            resetCmd[0] = CmdReset;
                            resetCmd[1] = ResetModeBootloader;
                            Array.Copy(ResetUnlockKey, 0, resetCmd, 2, ResetUnlockKey.Length);
                            _pcan.Send(CmdCanId, resetCmd);
                            ReceiveResponse(CmdReset, 200);
                        }
                        catch { }
                    }
                }
                catch
                {
                    // Timeout or error for this node — skip
                }
            }
        }
        finally
        {
            // Restore original CAN IDs and timeout settings
            CmdCanId = savedCmd;
            RspCanId = savedRsp;
            DataCanId = savedData;
            _pcan.ResponseCanId = savedRsp;
            TimeoutMultiplier = savedMultiplier;
            IsManualMultiplier = savedManual;
        }

        return devices;
    }

    /// <summary>
    /// Broadcast CMD_ENUMERATE and collect UID responses from all devices on the bus.
    /// Uses default CAN IDs (node 0) — all devices respond regardless of node_id.
    /// </summary>
    public List<EnumeratedDevice> Enumerate(int timeoutMs = 2000, Action<string>? log = null)
    {
        var devices = new List<EnumeratedDevice>();
        var seen = new HashSet<string>();
        var savedCmd = CmdCanId;
        var savedRsp = RspCanId;
        var savedData = DataCanId;

        try
        {
            ResetCanIds();
            _pcan.Drain(50);
            _pcan.Send(CmdCanId, new byte[] { CmdEnumerate });

            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                var remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
                var result = _pcan.Receive(Math.Min(remaining, 100), RspCanId);
                if (result == null) continue;

                var data = result.Value.Data;
                if (data.Length >= 7 && data[0] == CmdEnumerate)
                {
                    var uid = new byte[6];
                    Array.Copy(data, 1, uid, 0, 6);
                    var hex = BitConverter.ToString(uid).Replace("-", ":");
                    if (seen.Add(hex))
                    {
                        devices.Add(new EnumeratedDevice { Uid = uid });
                        log?.Invoke($"Enumerated device: {hex}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Enumerate error: {ex.Message}");
        }
        finally
        {
            CmdCanId = savedCmd;
            RspCanId = savedRsp;
            DataCanId = savedData;
            _pcan.ResponseCanId = savedRsp;
        }

        return devices;
    }

    private uint? QueryBytesReceived(CancellationToken ct = default)
    {
        _pcan.Send(CmdCanId, new byte[] { CmdGetStatus });
        var resp = ReceiveResponse(CmdGetStatus, Scaled(GetStatusTimeoutMs), ct);
        if (resp != null && resp.Length >= 7)
            return ReadUInt32LE(resp, 3);
        return null;
    }

    private byte[]? ReceiveResponse(byte cmdFilter, int timeoutMs, CancellationToken ct = default)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            int remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
            var result = _pcan.Receive(Math.Min(remaining, 100), RspCanId);
            if (result == null) continue;

            var (_, data) = result.Value;
            if (data.Length >= 1 && data[0] == cmdFilter)
                return data;
        }
        return null;
    }

    private static string FormatResp(byte[]? data)
    {
        if (data == null) return "no response";
        if (data.Length >= 2)
            return $"[{FormatStatus(data[1])}] {string.Join(" ", Array.ConvertAll(data, b => $"0x{b:X2}"))}";
        return string.Join(" ", Array.ConvertAll(data, b => $"0x{b:X2}"));
    }

    private static void WriteUInt32LE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteUInt24LE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
    }

    private static ushort ReadUInt16LE(byte[] data, int offset)
    {
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    private static uint ReadUInt32LE(byte[] buf, int offset)
    {
        return (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
    }

    /// <summary>
    /// Compute CRC32 per 4KB sector for a firmware binary (header + app data).
    /// Last sector is padded with 0xFF to a full 4KB to match flash contents.
    /// </summary>
    public static uint[] ComputeLocalSectorCrcs(byte[] firmware)
    {
        int sectorCount = (firmware.Length + FlashSectorSize - 1) / FlashSectorSize;
        var crcs = new uint[sectorCount];
        for (int i = 0; i < sectorCount; i++)
        {
            int offset = i * FlashSectorSize;
            int len = Math.Min(FlashSectorSize, firmware.Length - offset);
            byte[] sector = new byte[FlashSectorSize];
            Array.Fill(sector, (byte)0xFF);
            Array.Copy(firmware, offset, sector, 0, len);
            crcs[i] = ComputeCrc32(sector);
        }
        return crcs;
    }

    public static uint ComputeCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ 0xEDB88320;
                else
                    crc >>= 1;
            }
        }
        return crc ^ 0xFFFFFFFF;
    }

    // IProtocolTransport explicit implementation
    void IProtocolTransport.SendCommand(byte commandId, byte[] payload)
    {
        var frame = new byte[1 + payload.Length];
        frame[0] = commandId;
        Array.Copy(payload, 0, frame, 1, payload.Length);
        _pcan.Send(CmdCanId, frame);
    }

    (byte Status, byte[] Payload)? IProtocolTransport.ReceiveResponse(byte commandFilter, int timeoutMs)
    {
        var resp = ReceiveResponse(commandFilter, timeoutMs);
        if (resp == null) return null;
        byte status = resp.Length >= 2 ? resp[1] : (byte)0xFF;
        return (status, resp);
    }

    void IProtocolTransport.SendData(uint canId, byte[] data)
    {
        _pcan.Send(canId, data);
    }

    void IProtocolTransport.Drain(int durationMs)
    {
        _pcan.Drain(durationMs);
    }

    int IProtocolTransport.Scaled(int baseTimeoutMs)
    {
        return Scaled(baseTimeoutMs);
    }
}
