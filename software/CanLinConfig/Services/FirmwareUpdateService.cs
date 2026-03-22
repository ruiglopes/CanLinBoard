using CanBus;
using CanBus.Protocol;
using CanLinConfig.Models;
using static CanBus.ProtocolConstants;

namespace CanLinConfig.Services;

public enum FlashStage
{
    Connecting, Authenticating, QueryingBank, ComparingCRCs,
    Erasing, Flashing, Verifying, SwitchingBank, Resetting,
    Reconnecting, Complete, Failed
}

public record FlashProgress(
    FlashStage Stage,
    string Message,
    int Current,
    int Total,
    bool IsIndeterminate);

public record FlashOptions(
    string AdapterType,
    string ChannelName,
    uint OriginalBitrate,
    uint BootloaderBitrate,
    byte[]? Firmware,
    DfwContainer? DfwFile,
    byte[]? HmacKey,
    bool DeltaUpdate,
    bool WasConnected);

public class FirmwareUpdateService
{
    private ICanAdapter? _adapter;
    private BootloaderProtocol? _protocol;

    /// <summary>
    /// Callback for resume decision. Set by the VM.
    /// Returns true to resume, false to start fresh.
    /// </summary>
    public Func<uint, Task<bool>>? OnResumePrompt { get; set; }

    /// <summary>
    /// Callback for bitrate scan prompt. Returns true to try other bitrates.
    /// </summary>
    public Func<uint, Task<bool>>? OnBitrateScanPrompt { get; set; }

    /// <summary>
    /// Action to append a message to the log.
    /// </summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Set by ConnectWithRetry if user chose to resume an interrupted transfer.
    /// </summary>
    private bool _resumeChosen;
    private uint _resumeOffset;

    public async Task FlashAsync(
        FlashOptions options,
        IProgress<FlashProgress> progress,
        CancellationToken ct)
    {
        await Task.Run(() => FlashCore(options, progress, ct), ct);
    }

    private void FlashCore(FlashOptions options, IProgress<FlashProgress> progress, CancellationToken ct)
    {
        try
        {
            // Step 1: Create adapter and connect
            var info = ConnectWithRetry(options, progress, ct);

            // Step 2: Authenticate
            if (info.AuthRequired)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new(FlashStage.Authenticating, "Authenticating...", 0, 0, true));
                if (options.HmacKey == null)
                    throw new InvalidOperationException("Device requires authentication but no HMAC key provided");
                _protocol!.Authenticate(info.Nonce!, options.HmacKey, ct: ct);
                Log?.Invoke("Authentication successful");
            }

            // Step 3: Device identity
            if (FeatureRequirements.IsSupported("DeviceIdentity", info))
            {
                var deviceId = _protocol!.GetDeviceId();
                if (deviceId != null)
                    Log?.Invoke($"Device: UID={deviceId.UniqueIdHex}, CS0={deviceId.FlashCs0Summary}, CS1={deviceId.FlashCs1Summary}");
            }

            // Step 3b: CAN bus diagnostics
            if (FeatureRequirements.IsSupported("Diagnostics", info))
            {
                var diag = _protocol!.GetDiagnostics();
                if (diag.HasValue)
                    Log?.Invoke($"Bus diagnostics: parseErrors={diag.Value.ParseErrors}, rxOverflows={diag.Value.RxOverflows}, txRetries={diag.Value.TxRetries}");
            }

            // Step 4: Query active bank
            ct.ThrowIfCancellationRequested();
            progress.Report(new(FlashStage.QueryingBank, "Querying active bank...", 0, 0, true));
            byte targetBank;
            uint targetAddress;
            byte[] firmware;
            bool dualBankSupported = true;

            try
            {
                var (bankStatus, bankValue) = _protocol!.ConfigRead(CfgParamActiveBank);
                byte activeBank = bankValue[0];
                targetBank = (byte)(activeBank == 0 ? 1 : 0);
                targetAddress = targetBank == 0 ? BankABase : BankBBase;
                Log?.Invoke($"Active bank: {(activeBank == 0 ? "A" : "B")}, targeting Bank {(targetBank == 0 ? "A" : "B")} at 0x{targetAddress:X8}");
            }
            catch
            {
                Log?.Invoke("Dual-bank not supported, using Bank A");
                dualBankSupported = false;
                targetBank = 0;
                targetAddress = AppBase;
            }

            // Select firmware bytes
            if (options.DfwFile != null)
            {
                firmware = targetBank == 0 ? options.DfwFile.BankAFirmware : options.DfwFile.BankBFirmware;
            }
            else
            {
                firmware = options.Firmware!;
            }

            // Sign if needed
            if (options.HmacKey != null)
            {
                var header = AppHeader.TryParse(firmware);
                if (header != null && !header.HasSignature)
                {
                    Log?.Invoke("Signing firmware in-memory...");
                    firmware = AppHeader.SignFirmware(firmware, options.HmacKey);
                }
            }

            // Declared here so it's in scope at the verify label (reachable via goto from resume path)
            List<int>? changedSectors = null;

            // Step 4b: Resume handling (if user chose resume in ConnectWithRetry)
            if (_resumeChosen)
            {
                Log?.Invoke($"Resuming transfer from offset {_resumeOffset:N0} bytes");
                progress.Report(new(FlashStage.Flashing, $"Resuming from {_resumeOffset:N0} bytes...", (int)_resumeOffset, firmware.Length, false));
                _protocol!.SendDataResume(targetAddress, firmware, (int)_resumeOffset,
                    progress: (sent, total) =>
                        progress.Report(new(FlashStage.Flashing, $"Flashing: {sent:N0}/{total:N0} bytes", sent, total, false)),
                    log: msg => Log?.Invoke(msg),
                    ct: ct);
                Log?.Invoke("Data transfer complete (resumed)");
                // Skip to verify
                goto verify;
            }

            // Step 5: Delta comparison
            if (options.DeltaUpdate)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new(FlashStage.ComparingCRCs, "Comparing sector CRCs...", 0, 0, true));
                try
                {
                    var remoteCrcs = _protocol!.GetSectorCrcs(targetAddress);
                    if (remoteCrcs != null)
                    {
                        var localCrcs = BootloaderProtocol.ComputeLocalSectorCrcs(firmware);
                        changedSectors = new List<int>();
                        int sectorCount = Math.Max(remoteCrcs.Length, localCrcs.Length);
                        for (int i = 0; i < sectorCount; i++)
                        {
                            uint? remote = i < remoteCrcs.Length ? remoteCrcs[i] : null;
                            uint local = i < localCrcs.Length ? localCrcs[i] : 0xFFFFFFFF;
                            if (remote == null || remote.Value != local)
                                changedSectors.Add(i);
                        }

                        if (changedSectors.Count == 0)
                        {
                            Log?.Invoke("Firmware is identical — no update needed");
                            progress.Report(new(FlashStage.Complete, "Firmware already up to date", 0, 0, false));
                            return;
                        }

                        Log?.Invoke($"Delta: {changedSectors.Count}/{sectorCount} sectors changed");
                    }
                    else
                    {
                        Log?.Invoke("Sector CRCs not available — using full flash");
                    }
                }
                catch
                {
                    Log?.Invoke("Delta comparison failed — using full flash");
                }
            }

            // Step 6: Erase
            ct.ThrowIfCancellationRequested();
            if (changedSectors != null)
            {
                // Delta erase
                for (int i = 0; i < changedSectors.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    int sectorIdx = changedSectors[i];
                    uint sectorAddr = targetAddress + (uint)(sectorIdx * FlashSectorSize);
                    progress.Report(new(FlashStage.Erasing, $"Erasing sector {i + 1}/{changedSectors.Count}", i + 1, changedSectors.Count, false));
                    _protocol!.Erase(sectorAddr, (uint)FlashSectorSize, ct: ct);
                }
            }
            else
            {
                // Full erase
                progress.Report(new(FlashStage.Erasing, "Erasing flash...", 0, 0, true));
                _protocol!.Erase(targetAddress, (uint)firmware.Length,
                    progress: (done, total) =>
                        progress.Report(new(FlashStage.Erasing, $"Erasing sector {done}/{total}", done, total, false)),
                    log: msg => Log?.Invoke(msg),
                    ct: ct);
            }
            Log?.Invoke("Erase complete");

            // Step 7: Send data
            ct.ThrowIfCancellationRequested();
            if (changedSectors != null)
            {
                // Delta send
                int totalBytes = 0;
                int sentBytes = 0;
                foreach (int idx in changedSectors)
                {
                    int dataOffset = idx * FlashSectorSize;
                    int dataLen = Math.Min(FlashSectorSize, firmware.Length - dataOffset);
                    if (dataLen > 0) totalBytes += dataLen;
                }

                foreach (int sectorIdx in changedSectors)
                {
                    ct.ThrowIfCancellationRequested();
                    uint sectorAddr = targetAddress + (uint)(sectorIdx * FlashSectorSize);
                    int dataOffset = sectorIdx * FlashSectorSize;
                    int dataLen = Math.Min(FlashSectorSize, firmware.Length - dataOffset);
                    if (dataLen <= 0) continue;

                    var sectorData = new byte[dataLen];
                    Array.Copy(firmware, dataOffset, sectorData, 0, dataLen);

                    _protocol!.SendData(sectorAddr, sectorData,
                        progress: (sent, total) =>
                            progress.Report(new(FlashStage.Flashing, $"Flashing: {sentBytes + sent:N0}/{totalBytes:N0} bytes",
                                sentBytes + sent, totalBytes, false)),
                        log: msg => Log?.Invoke(msg),
                        ct: ct);
                    sentBytes += dataLen;
                }
            }
            else
            {
                // Full send
                _protocol!.SendData(targetAddress, firmware,
                    progress: (sent, total) =>
                        progress.Report(new(FlashStage.Flashing, $"Flashing: {sent:N0}/{total:N0} bytes", sent, total, false)),
                    log: msg => Log?.Invoke(msg),
                    ct: ct);
            }
            Log?.Invoke("Data transfer complete");

            // Step 8: Verify
            verify:
            ct.ThrowIfCancellationRequested();
            progress.Report(new(FlashStage.Verifying, "Verifying...", 0, 0, true));
            if (changedSectors != null)
            {
                var (pass, crc) = _protocol!.VerifyApp(ct: ct);
                if (!pass)
                    throw new ProtocolException($"Verification failed: device CRC 0x{crc:X8}", StatusCrcMismatch);
                Log?.Invoke($"Verification passed (CRC 0x{crc:X8})");
            }
            else
            {
                var (match, crc) = _protocol!.Verify(targetAddress, firmware, ct: ct);
                if (!match)
                    throw new ProtocolException($"Verification failed: device CRC 0x{crc:X8}", StatusCrcMismatch);
                Log?.Invoke($"Verification passed (CRC 0x{crc:X8})");
            }

            // Step 9: Switch bank
            if (dualBankSupported)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new(FlashStage.SwitchingBank, $"Switching to Bank {(targetBank == 0 ? "A" : "B")}...", 0, 0, true));
                var switchStatus = _protocol!.SwitchBank(targetBank);
                if (switchStatus != ConfigStatus.Ok)
                    throw new InvalidOperationException($"Bank switch failed: {switchStatus}");
                Log?.Invoke($"Switched active bank to {(targetBank == 0 ? "A" : "B")}");
            }

            // Step 10: Reset
            progress.Report(new(FlashStage.Resetting, "Resetting to application...", 0, 0, true));
            _protocol!.Reset(ResetModeApp);
            Log?.Invoke("Device reset to application mode");

            progress.Report(new(FlashStage.Complete, "Firmware update complete!", 0, 0, false));
        }
        catch (OperationCanceledException)
        {
            Log?.Invoke("Update cancelled by user");
            try { _protocol?.Abort(); } catch { }
            progress.Report(new(FlashStage.Failed, "Cancelled", 0, 0, false));
            throw;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Error: {ex.Message}");
            if (ex is ProtocolException pe && pe.StatusCode.HasValue)
            {
                var hint = ProtocolConstants.GetHint(pe.StatusCode.Value);
                if (hint != null) Log?.Invoke($"Hint: {hint}");
            }
            try { _protocol?.Abort(); } catch { }
            progress.Report(new(FlashStage.Failed, ex.Message, 0, 0, false));
            throw;
        }
        finally
        {
            _adapter?.Shutdown();
            _adapter = null;
            _protocol = null;
        }
    }

    private BootloaderInfo ConnectWithRetry(FlashOptions options, IProgress<FlashProgress> progress, CancellationToken ct)
    {
        _adapter = AdapterFactory.Create(options.AdapterType);
        int channelIndex = AdapterFactory.FindChannelIndex(_adapter, options.ChannelName);
        int bitrateIndex = AdapterFactory.FindBitrateIndex(_adapter, options.BootloaderBitrate);

        _adapter.DebugMessageReceived += data =>
        {
            var msg = DebugMessage.TryParse(data);
            if (msg != null) Log?.Invoke($"[{msg.LevelName}] {msg.Tag}: {msg.Description}");
        };

        _adapter.Initialize(channelIndex, bitrateIndex);
        _protocol = new BootloaderProtocol(_adapter);

        progress.Report(new(FlashStage.Connecting, $"Connecting at {options.BootloaderBitrate / 1000} kbps...", 0, 0, true));

        // Try primary bitrate
        var info = TryConnect(10, ct);
        if (info != null) return info;

        // Offer bitrate scan
        if (OnBitrateScanPrompt != null)
        {
            bool doScan = OnBitrateScanPrompt(options.BootloaderBitrate).GetAwaiter().GetResult();
            if (doScan)
            {
                foreach (uint br in AdapterFactory.GetScanBitrates(options.BootloaderBitrate))
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Report(new(FlashStage.Connecting, $"Trying {br / 1000} kbps...", 0, 0, true));
                    _adapter.Shutdown();
                    _adapter = AdapterFactory.Create(options.AdapterType);
                    _adapter.Initialize(channelIndex, AdapterFactory.FindBitrateIndex(_adapter, br));
                    _protocol = new BootloaderProtocol(_adapter);

                    info = TryConnect(3, ct);
                    if (info != null)
                    {
                        Log?.Invoke($"Connected at {br / 1000} kbps");
                        return info;
                    }
                }
            }
        }

        throw new TimeoutException("Device not responding at any standard bitrate — check CAN connection and power");
    }

    private BootloaderInfo? TryConnect(int maxAttempts, CancellationToken ct)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = _protocol!.Connect(ct);

                // Check for resume state
                if (FeatureRequirements.IsSupported("TransferResume", info))
                {
                    var (state, _, bytesReceived, _) = _protocol.GetStatus();
                    if (state == 4 && bytesReceived > 0 && OnResumePrompt != null) // state 4 = RECEIVING
                    {
                        bool doResume = OnResumePrompt(bytesReceived).GetAwaiter().GetResult();
                        if (doResume)
                        {
                            var offset = _protocol.QueryResumeOffset();
                            if (offset.HasValue && offset.Value > 0)
                            {
                                _resumeChosen = true;
                                _resumeOffset = offset.Value;
                            }
                        }
                        else
                        {
                            _protocol.Abort();
                        }
                    }
                }

                return info;
            }
            catch (TimeoutException)
            {
                Thread.Sleep(200);
            }
        }
        return null;
    }
}
