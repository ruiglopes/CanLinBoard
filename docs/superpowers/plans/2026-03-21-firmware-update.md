# Firmware Update via Config Tool — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add firmware flashing capability to the CanLinConfig WPF tool, supporting authentication, dual-bank, delta updates, resume, and `.dfw` container format.

**Architecture:** A modal `FirmwareUpdateWindow` dialog launches from the main window. `FirmwareUpdateService` orchestrates the flash workflow using the bootloader protocol libraries (`lib/CanBus.*`) with their own CAN adapter instances (separate from the config tool's adapters). The config tool disconnects its adapter before flashing and reconnects after.

**Tech Stack:** .NET 8, WPF, CommunityToolkit.Mvvm, CanBus.Protocol lib, MahApps.Metro

**Spec:** `docs/superpowers/specs/2026-03-21-firmware-update-design.md`

---

## File Map

### New Files

| File | Responsibility |
|------|---------------|
| `software/CanLinConfig/Models/AppHeader.cs` | Firmware binary parser — validates magic, CRC, size, entry point, HMAC signature |
| `software/CanLinConfig/Models/DfwContainer.cs` | Dual-bank `.dfw` container parser — splits into Bank A + Bank B images |
| `software/CanLinConfig/Services/AdapterFactory.cs` | Maps config tool adapter name → lib `CanBus.ICanAdapter`, resolves channel indices |
| `software/CanLinConfig/Services/FirmwareUpdateSettings.cs` | Persists key path, firmware path, bootloader bitrate to `%AppData%` JSON |
| `software/CanLinConfig/Services/FirmwareUpdateService.cs` | Flash workflow orchestrator — connect, auth, erase, send, verify, bank switch, reset |
| `software/CanLinConfig/ViewModels/FirmwareUpdateViewModel.cs` | MVVM view model — file/key selection, progress binding, start/cancel/close commands |
| `software/CanLinConfig/Views/FirmwareUpdateWindow.xaml` | WPF dialog — setup panel, progress panel, log panel |

### Modified Files

| File | Change |
|------|--------|
| `software/CanLinConfig.sln` | Add 3 lib project entries |
| `software/CanLinConfig/CanLinConfig.csproj` | Add 3 `<ProjectReference>`, upgrade Peak.PCANBasic.NET to 4.10.1.968 |
| `software/CanLinConfig/ViewModels/MainViewModel.cs` | Add `UpdateFirmwareCommand`, `DisconnectForFirmwareUpdate()`, `ReconnectAfterFirmwareUpdate()` |
| `software/CanLinConfig/Views/MainWindow.xaml` | Add "Update Firmware" button in bottom bar |

---

## Task 1: Add lib project references and fix NuGet version

**Files:**
- Modify: `software/CanLinConfig.sln`
- Modify: `software/CanLinConfig/CanLinConfig.csproj`

- [ ] **Step 1: Add lib projects to the solution**

```bash
cd software
dotnet sln add ../lib/CanBus.Abstractions/CanBus.Abstractions.csproj
dotnet sln add ../lib/CanBus.Adapters/CanBus.Adapters.csproj
dotnet sln add ../lib/CanBus.Protocol/CanBus.Protocol.csproj
```

- [ ] **Step 2: Add project references to CanLinConfig.csproj**

Add this `<ItemGroup>` to `software/CanLinConfig/CanLinConfig.csproj` after the existing `<ItemGroup>` with `<PackageReference>`:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\lib\CanBus.Abstractions\CanBus.Abstractions.csproj" />
  <ProjectReference Include="..\..\lib\CanBus.Adapters\CanBus.Adapters.csproj" />
  <ProjectReference Include="..\..\lib\CanBus.Protocol\CanBus.Protocol.csproj" />
</ItemGroup>
```

- [ ] **Step 3: Upgrade Peak.PCANBasic.NET to match lib version**

In `software/CanLinConfig/CanLinConfig.csproj`, change:

```xml
<PackageReference Include="Peak.PCANBasic.NET" Version="4.9.0.942" />
```

to:

```xml
<PackageReference Include="Peak.PCANBasic.NET" Version="4.10.1.968" />
```

- [ ] **Step 4: Verify build**

```bash
cd software
dotnet build CanLinConfig.sln
```

Expected: Build succeeds with 0 errors. Warnings about unused references are OK at this stage.

- [ ] **Step 5: Commit**

```bash
git add software/CanLinConfig.sln software/CanLinConfig/CanLinConfig.csproj
git commit -m "Add bootloader protocol lib references and align PCAN NuGet version"
```

---

## Task 2: Copy AppHeader and DfwContainer models

**Files:**
- Create: `software/CanLinConfig/Models/AppHeader.cs`
- Create: `software/CanLinConfig/Models/DfwContainer.cs`

- [ ] **Step 1: Copy AppHeader.cs from bootloader programmer**

Copy `C:\Projects\Embedded\Bootloader\programmer\CanFlashProgrammer\Models\AppHeader.cs` to `software/CanLinConfig/Models/AppHeader.cs`.

Change the namespace from `CanFlashProgrammer.Models` to `CanLinConfig.Models`:

```csharp
namespace CanLinConfig.Models;
```

Everything else stays identical — `TryParse`, `SignFirmware`, `ComputeCrc32`, `CreateHeader`, all properties.

- [ ] **Step 2: Copy DfwContainer.cs from bootloader programmer**

Copy `C:\Projects\Embedded\Bootloader\programmer\CanFlashProgrammer\Models\DfwContainer.cs` to `software/CanLinConfig/Models/DfwContainer.cs`.

Change namespace to `CanLinConfig.Models`. Update the `using` — remove `using CanBus;` (not needed here) and ensure the `AppHeader` reference resolves within the same namespace.

- [ ] **Step 3: Verify build**

```bash
cd software && dotnet build CanLinConfig.sln
```

Expected: 0 errors. Both models compile.

- [ ] **Step 4: Commit**

```bash
git add software/CanLinConfig/Models/AppHeader.cs software/CanLinConfig/Models/DfwContainer.cs
git commit -m "Add AppHeader and DfwContainer firmware file parsers"
```

---

## Task 3: Implement AdapterFactory

**Files:**
- Create: `software/CanLinConfig/Services/AdapterFactory.cs`

- [ ] **Step 1: Create AdapterFactory.cs**

```csharp
using CanBus;
using CanBus.Adapters;

namespace CanLinConfig.Services;

/// <summary>
/// Maps config tool adapter type names to bootloader protocol lib adapter instances.
/// </summary>
public static class AdapterFactory
{
    private static readonly uint[] StandardBitrates = [500000, 250000, 1000000, 125000];

    public static ICanAdapter Create(string adapterType) => adapterType switch
    {
        "PCAN"      => new PcanService(),
        "Vector XL" => new VectorService(),
        "Kvaser"    => new KvaserService(),
        "SLCAN"     => new SlcanService(),
        _           => throw new ArgumentException($"Unknown adapter type: {adapterType}")
    };

    /// <summary>
    /// Find the channel index in the lib adapter matching the config tool's channel name.
    /// </summary>
    public static int FindChannelIndex(ICanAdapter adapter, string channelName)
    {
        var names = adapter.ChannelNames;

        // Exact match (case-insensitive)
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i].Equals(channelName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        // Substring fallback (Vector XL channel name formats may differ)
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i].Contains(channelName, StringComparison.OrdinalIgnoreCase) ||
                channelName.Contains(names[i], StringComparison.OrdinalIgnoreCase))
                return i;
        }

        throw new ArgumentException($"Channel '{channelName}' not found in {adapter.AdapterName}");
    }

    /// <summary>
    /// Find the bitrate index in the lib adapter matching the target bitrate value.
    /// </summary>
    public static int FindBitrateIndex(ICanAdapter adapter, uint bitrate)
    {
        var names = adapter.BitrateNames;
        string target = bitrate.ToString();

        for (int i = 0; i < names.Length; i++)
        {
            // BitrateNames are like "500 kbit/s" — extract numeric part
            var numeric = new string(names[i].Where(c => char.IsDigit(c)).ToArray());
            // Compare as kbps (names use kbit/s) and as raw value
            if (numeric == (bitrate / 1000).ToString() || numeric == target)
                return i;
        }

        throw new ArgumentException($"Bitrate {bitrate} not found in {adapter.AdapterName}");
    }

    /// <summary>
    /// Returns standard bitrates to try during auto-scan, excluding the one already tried.
    /// </summary>
    public static uint[] GetScanBitrates(uint excludeBitrate)
    {
        return StandardBitrates.Where(b => b != excludeBitrate).ToArray();
    }
}
```

- [ ] **Step 2: Verify build**

```bash
cd software && dotnet build CanLinConfig.sln
```

- [ ] **Step 3: Commit**

```bash
git add software/CanLinConfig/Services/AdapterFactory.cs
git commit -m "Add AdapterFactory for bootloader lib adapter creation"
```

---

## Task 4: Implement FirmwareUpdateSettings

**Files:**
- Create: `software/CanLinConfig/Services/FirmwareUpdateSettings.cs`

- [ ] **Step 1: Create FirmwareUpdateSettings.cs**

```csharp
using System.IO;
using System.Text.Json;

namespace CanLinConfig.Services;

public class FirmwareUpdateSettings
{
    public string? LastKeyFilePath { get; set; }
    public string? LastFirmwarePath { get; set; }
    public uint BootloaderBitrate { get; set; } = 500000;

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanLinConfig");
    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "firmware-update.json");

    public static FirmwareUpdateSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<FirmwareUpdateSettings>(json,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? new();
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }
}
```

- [ ] **Step 2: Verify build**

```bash
cd software && dotnet build CanLinConfig.sln
```

- [ ] **Step 3: Commit**

```bash
git add software/CanLinConfig/Services/FirmwareUpdateSettings.cs
git commit -m "Add FirmwareUpdateSettings for persisting update dialog preferences"
```

---

## Task 5: Add MainViewModel handoff methods

**Files:**
- Modify: `software/CanLinConfig/ViewModels/MainViewModel.cs`

These methods must exist before `FirmwareUpdateViewModel` (Task 7) can compile.

- [ ] **Step 1: Add DisconnectForFirmwareUpdate and ReconnectAfterFirmwareUpdate**

Add the following to `software/CanLinConfig/ViewModels/MainViewModel.cs` after the `EnterBootloader()` method (around line 264):

```csharp
    /// <summary>
    /// Enters bootloader mode and fully tears down the config tool adapter.
    /// Called by FirmwareUpdateService before starting flash.
    /// </summary>
    public async Task DisconnectForFirmwareUpdate()
    {
        if (_protocol != null)
        {
            try
            {
                await _protocol.EnterBootloaderAsync();
            }
            catch { }
        }
        Disconnect();
    }

    /// <summary>
    /// Reconnects the config tool adapter after firmware update.
    /// Returns true if reconnect succeeded.
    /// </summary>
    public async Task<bool> ReconnectAfterFirmwareUpdate()
    {
        try
        {
            await ConnectAsync();
            return IsConnected;
        }
        catch
        {
            return false;
        }
    }
```

- [ ] **Step 2: Verify build**

```bash
cd software && dotnet build CanLinConfig.sln
```

Expected: 0 errors. These are new public methods with no callers yet.

- [ ] **Step 3: Commit**

```bash
git add software/CanLinConfig/ViewModels/MainViewModel.cs
git commit -m "Add firmware update adapter handoff methods to MainViewModel"
```

---

## Task 6: Implement FirmwareUpdateService

**Files:**
- Create: `software/CanLinConfig/Services/FirmwareUpdateService.cs`

This is the core orchestrator. It runs the entire flash workflow on a background thread.

- [ ] **Step 1: Create the FlashProgress and FlashOptions types**

Create `software/CanLinConfig/Services/FirmwareUpdateService.cs` with the types and class shell:

```csharp
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
}
```

- [ ] **Step 2: Implement the connect logic with retry and bitrate scan**

Add to `FirmwareUpdateService`:

```csharp
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
            List<int>? changedSectors = null;
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
                    _protocol!.Erase(sectorAddr, FlashSectorSize, ct: ct);
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
                    int dataOffset = idx * (int)FlashSectorSize;
                    int dataLen = Math.Min((int)FlashSectorSize, firmware.Length - dataOffset);
                    if (dataLen > 0) totalBytes += dataLen;
                }

                foreach (int sectorIdx in changedSectors)
                {
                    ct.ThrowIfCancellationRequested();
                    uint sectorAddr = targetAddress + (uint)(sectorIdx * FlashSectorSize);
                    int dataOffset = sectorIdx * (int)FlashSectorSize;
                    int dataLen = Math.Min((int)FlashSectorSize, firmware.Length - dataOffset);
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
            var msg = CanBus.Models.DebugMessage.TryParse(data);
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
```

- [ ] **Step 3: Verify build**

```bash
cd software && dotnet build CanLinConfig.sln
```

Expected: 0 errors. There may be warnings about some `ProtocolConstants` members — these depend on the exact lib API. Fix any compile errors by checking the actual member names in `lib/CanBus.Abstractions/ProtocolConstants.cs` and `lib/CanBus.Protocol/BootloaderProtocol.cs`.

- [ ] **Step 4: Commit**

```bash
git add software/CanLinConfig/Services/FirmwareUpdateService.cs
git commit -m "Add FirmwareUpdateService flash workflow orchestrator"
```

---

## Task 7: Implement FirmwareUpdateViewModel

**Files:**
- Create: `software/CanLinConfig/ViewModels/FirmwareUpdateViewModel.cs`

- [ ] **Step 1: Create the view model**

```csharp
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Models;
using CanLinConfig.Services;

namespace CanLinConfig.ViewModels;

public partial class FirmwareUpdateViewModel : ObservableObject
{
    private readonly MainViewModel _mainVm;
    private readonly FirmwareUpdateSettings _settings;
    private CancellationTokenSource? _cts;
    private byte[]? _firmware;
    private DfwContainer? _dfwFile;
    private byte[]? _hmacKey;

    [ObservableProperty] private string _firmwarePath = "";
    [ObservableProperty] private string _firmwareStatus = "";
    [ObservableProperty] private bool _firmwareValid;
    [ObservableProperty] private string _keyStatus = "";
    [ObservableProperty] private bool _keyLoaded;
    [ObservableProperty] private bool _keyRequired;
    [ObservableProperty] private string _keyHexInput = "";
    [ObservableProperty] private bool _useHexInput;
    [ObservableProperty] private bool _rememberKeyPath;
    [ObservableProperty] private string _selectedBitrate = "500000";
    [ObservableProperty] private bool _deltaUpdate = true;
    [ObservableProperty] private string _targetBankText = "";

    // Progress
    [ObservableProperty] private string _stageText = "";
    [ObservableProperty] private string _counterText = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string _logText = "";

    // State
    [ObservableProperty] private bool _isFlashing;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canClose = true;

    public string[] BitrateOptions { get; } = ["125000", "250000", "500000", "1000000"];

    public FirmwareUpdateViewModel(MainViewModel mainVm)
    {
        _mainVm = mainVm;
        _settings = FirmwareUpdateSettings.Load();

        // Restore settings
        SelectedBitrate = _settings.BootloaderBitrate.ToString();
        if (_settings.LastFirmwarePath != null && File.Exists(_settings.LastFirmwarePath))
            LoadFirmwareFile(_settings.LastFirmwarePath);
        if (_settings.LastKeyFilePath != null && File.Exists(_settings.LastKeyFilePath))
            LoadKeyFile(_settings.LastKeyFilePath);

        RememberKeyPath = _settings.LastKeyFilePath != null;
    }

    [RelayCommand]
    private void BrowseFirmware()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Firmware (*.dfw;*.bin)|*.dfw;*.bin|Dual-Bank (*.dfw)|*.dfw|Binary (*.bin)|*.bin|All files (*.*)|*.*",
            Title = "Select Firmware File"
        };
        if (dialog.ShowDialog() != true) return;
        LoadFirmwareFile(dialog.FileName);
    }

    private void LoadFirmwareFile(string path)
    {
        FirmwarePath = path;
        _firmware = null;
        _dfwFile = null;
        FirmwareValid = false;

        try
        {
            if (path.EndsWith(".dfw", StringComparison.OrdinalIgnoreCase))
            {
                var data = File.ReadAllBytes(path);
                var error = DfwContainer.Validate(data);
                if (error != null)
                {
                    FirmwareStatus = $"Error: {error}";
                    UpdateCanStart();
                    return;
                }
                _dfwFile = DfwContainer.TryParse(data);
                FirmwareStatus = $"Dual-bank v{_dfwFile!.VersionString} — " +
                    $"Bank A ({_dfwFile.BankAFirmware.Length:N0} bytes) + " +
                    $"Bank B ({_dfwFile.BankBFirmware.Length:N0} bytes)";
                FirmwareValid = true;
            }
            else
            {
                var header = AppHeader.TryParse(path);
                if (header == null)
                {
                    FirmwareStatus = "Error: Failed to read firmware file";
                    UpdateCanStart();
                    return;
                }
                if (!header.MagicValid) { FirmwareStatus = "Error: Not a valid firmware file (bad magic)"; UpdateCanStart(); return; }
                if (!header.CrcValid) { FirmwareStatus = "Error: CRC mismatch — file may be corrupted"; UpdateCanStart(); return; }
                if (!header.SizeValid) { FirmwareStatus = "Error: Invalid firmware size"; UpdateCanStart(); return; }
                if (!header.EntryPointValid) { FirmwareStatus = "Error: Invalid entry point"; UpdateCanStart(); return; }

                _firmware = header.FullBinary;
                FirmwareStatus = $"v{header.VersionString} — {header.Size:N0} bytes, CRC 0x{header.Crc32:X8}, {(header.HasSignature ? "Signed" : "Unsigned")}";
                FirmwareValid = true;
            }

            _settings.LastFirmwarePath = path;
            _settings.Save();
        }
        catch (Exception ex)
        {
            FirmwareStatus = $"Error: {ex.Message}";
        }
        UpdateCanStart();
    }

    [RelayCommand]
    private void BrowseKey()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Key files (*.key;*.bin)|*.key;*.bin|All files (*.*)|*.*",
            Title = "Select HMAC Key File"
        };
        if (dialog.ShowDialog() != true) return;
        LoadKeyFile(dialog.FileName);
    }

    private void LoadKeyFile(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length != 32)
            {
                KeyStatus = $"Error: Key must be exactly 32 bytes (got {data.Length})";
                _hmacKey = null;
                KeyLoaded = false;
                UpdateCanStart();
                return;
            }
            _hmacKey = data;
            KeyStatus = $"Key loaded: {Path.GetFileName(path)} (32 bytes)";
            KeyLoaded = true;

            if (RememberKeyPath)
            {
                _settings.LastKeyFilePath = path;
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            KeyStatus = $"Error: {ex.Message}";
            _hmacKey = null;
            KeyLoaded = false;
        }
        UpdateCanStart();
    }

    partial void OnKeyHexInputChanged(string value)
    {
        if (!UseHexInput) return;
        var hex = value.Replace(" ", "").Replace("-", "");
        if (hex.Length == 64 && hex.All(c => Uri.IsHexDigit(c)))
        {
            _hmacKey = Convert.FromHexString(hex);
            KeyStatus = "Key loaded from hex input (32 bytes)";
            KeyLoaded = true;
        }
        else
        {
            _hmacKey = null;
            KeyLoaded = false;
            KeyStatus = hex.Length > 0 ? $"Need 64 hex chars (got {hex.Length})" : "";
        }
        UpdateCanStart();
    }

    partial void OnSelectedBitrateChanged(string value)
    {
        if (uint.TryParse(value, out uint br))
        {
            _settings.BootloaderBitrate = br;
            _settings.Save();
        }
    }

    private void UpdateCanStart()
    {
        CanStart = FirmwareValid && !IsFlashing && (!KeyRequired || KeyLoaded);
    }

    private void AppendLog(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LogText += $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n";
        });
    }

    [RelayCommand]
    private async Task StartFlash()
    {
        if (!FirmwareValid) return;

        IsFlashing = true;
        CanStart = false;
        CanClose = false;
        LogText = "";
        _cts = new CancellationTokenSource();

        var options = new FlashOptions(
            AdapterType: _mainVm.SelectedAdapter,
            ChannelName: _mainVm.SelectedChannel,
            OriginalBitrate: uint.TryParse(_mainVm.SelectedBitrate, out uint br) ? br : 500000,
            BootloaderBitrate: uint.TryParse(SelectedBitrate, out uint blBr) ? blBr : 500000,
            Firmware: _firmware,
            DfwFile: _dfwFile,
            HmacKey: _hmacKey,
            DeltaUpdate: DeltaUpdate,
            WasConnected: _mainVm.IsConnected);

        // Disconnect config tool if connected
        if (_mainVm.IsConnected)
        {
            AppendLog("Entering bootloader mode...");
            await _mainVm.DisconnectForFirmwareUpdate();
            await Task.Delay(500);
        }

        var service = new FirmwareUpdateService
        {
            Log = AppendLog,
            OnBitrateScanPrompt = (failedBitrate) =>
            {
                var result = MessageBox.Show(
                    $"Connection failed at {failedBitrate / 1000} kbps.\nTry other standard bitrates?",
                    "Bitrate Scan", MessageBoxButton.YesNo, MessageBoxImage.Question);
                return Task.FromResult(result == MessageBoxResult.Yes);
            },
            OnResumePrompt = (bytesReceived) =>
            {
                var result = MessageBox.Show(
                    $"Previous transfer interrupted at {bytesReceived:N0} bytes.\nResume from last checkpoint?",
                    "Resume Transfer", MessageBoxButton.YesNo, MessageBoxImage.Question);
                return Task.FromResult(result == MessageBoxResult.Yes);
            }
        };

        var progressHandler = new Progress<FlashProgress>(p =>
        {
            StageText = p.Message;
            IsIndeterminate = p.IsIndeterminate;
            if (!p.IsIndeterminate && p.Total > 0)
            {
                ProgressValue = (double)p.Current / p.Total * 100;
                CounterText = p.Stage switch
                {
                    FlashStage.Erasing => $"Sector {p.Current}/{p.Total}",
                    FlashStage.Flashing => $"{p.Current:N0} / {p.Total:N0} bytes",
                    _ => ""
                };
            }
            else
            {
                CounterText = "";
            }

            if (p.Stage == FlashStage.Complete || p.Stage == FlashStage.Failed)
            {
                IsFlashing = false;
                CanClose = true;
                CanStart = p.Stage == FlashStage.Failed && FirmwareValid;
            }
        });

        try
        {
            await service.FlashAsync(options, progressHandler, _cts.Token);

            // Reconnect config tool
            if (options.WasConnected)
            {
                AppendLog("Reconnecting config tool...");
                await Task.Delay(1000); // Wait for app to boot
                var reconnected = await _mainVm.ReconnectAfterFirmwareUpdate();
                if (reconnected)
                    AppendLog("Config tool reconnected");
                else
                    AppendLog("Reconnect failed — reconnect manually");
            }
        }
        catch (OperationCanceledException)
        {
            // Already handled in service
        }
        catch (Exception ex)
        {
            AppendLog($"Flash failed: {ex.Message}");
        }
        finally
        {
            IsFlashing = false;
            CanClose = true;
            UpdateCanStart();
        }
    }

    [RelayCommand]
    private void CancelFlash()
    {
        _cts?.Cancel();
    }
}
```

- [ ] **Step 2: Verify build**

```bash
cd software && dotnet build CanLinConfig.sln
```

Expected: Build errors for `DisconnectForFirmwareUpdate` and `ReconnectAfterFirmwareUpdate` — these don't exist on `MainViewModel` yet. That's expected; they are added in Task 8.

- [ ] **Step 3: Commit**

```bash
git add software/CanLinConfig/ViewModels/FirmwareUpdateViewModel.cs
git commit -m "Add FirmwareUpdateViewModel with file/key management and flash control"
```

---

## Task 8: Create FirmwareUpdateWindow XAML

**Files:**
- Create: `software/CanLinConfig/Views/FirmwareUpdateWindow.xaml`
- Create: `software/CanLinConfig/Views/FirmwareUpdateWindow.xaml.cs`

- [ ] **Step 1: Create FirmwareUpdateWindow.xaml**

```xml
<mah:MetroWindow x:Class="CanLinConfig.Views.FirmwareUpdateWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:mah="http://metro.mahapps.com/winfx/xaml/controls"
        Title="Firmware Update" Height="650" Width="600"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize"
        GlowBrush="{DynamicResource MahApps.Brushes.Accent}"
        TitleCharacterCasing="Normal">
    <Grid Margin="15">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <!-- Setup Panel -->
        <GroupBox Header="Setup" Grid.Row="0" Margin="0,0,0,10">
            <StackPanel Margin="5">
                <!-- Firmware file -->
                <TextBlock Text="Firmware File" FontWeight="SemiBold" Margin="0,0,0,3" />
                <DockPanel Margin="0,0,0,3">
                    <Button Content="Browse..." Command="{Binding BrowseFirmwareCommand}"
                            DockPanel.Dock="Right" Width="80" Margin="5,0,0,0"
                            IsEnabled="{Binding IsFlashing, Converter={StaticResource InverseBoolConverter}}" />
                    <TextBox Text="{Binding FirmwarePath, Mode=OneWay}" IsReadOnly="True" />
                </DockPanel>
                <TextBlock Text="{Binding FirmwareStatus}" Margin="0,0,0,8"
                           Foreground="{Binding FirmwareValid, Converter={StaticResource BoolToGreenRedBrushConverter}}" />

                <!-- HMAC Key -->
                <TextBlock Text="HMAC Key" FontWeight="SemiBold" Margin="0,0,0,3" />
                <DockPanel Margin="0,0,0,3" Visibility="{Binding UseHexInput, Converter={StaticResource InverseBoolToVisConverter}}">
                    <Button Content="Browse..." Command="{Binding BrowseKeyCommand}"
                            DockPanel.Dock="Right" Width="80" Margin="5,0,0,0"
                            IsEnabled="{Binding IsFlashing, Converter={StaticResource InverseBoolConverter}}" />
                    <TextBox Text="{Binding KeyStatus, Mode=OneWay}" IsReadOnly="True" />
                </DockPanel>
                <TextBox Text="{Binding KeyHexInput, UpdateSourceTrigger=PropertyChanged}"
                         Visibility="{Binding UseHexInput, Converter={StaticResource BoolToVisConverter}}"
                         mah:TextBoxHelper.Watermark="Enter 64 hex characters (32 bytes)"
                         Margin="0,0,0,3"
                         IsEnabled="{Binding IsFlashing, Converter={StaticResource InverseBoolConverter}}" />
                <StackPanel Orientation="Horizontal" Margin="0,0,0,8">
                    <CheckBox Content="Enter manually" IsChecked="{Binding UseHexInput}" Margin="0,0,15,0" />
                    <CheckBox Content="Remember key path" IsChecked="{Binding RememberKeyPath}" />
                </StackPanel>

                <!-- Bootloader bitrate + Delta -->
                <StackPanel Orientation="Horizontal" Margin="0,0,0,5">
                    <TextBlock Text="Bootloader Bitrate:" VerticalAlignment="Center" Margin="0,0,5,0" />
                    <ComboBox ItemsSource="{Binding BitrateOptions}" SelectedItem="{Binding SelectedBitrate}"
                              Width="120" IsEnabled="{Binding IsFlashing, Converter={StaticResource InverseBoolConverter}}" />
                    <CheckBox Content="Delta update" IsChecked="{Binding DeltaUpdate}" Margin="20,0,0,0"
                              VerticalAlignment="Center"
                              IsEnabled="{Binding IsFlashing, Converter={StaticResource InverseBoolConverter}}" />
                </StackPanel>

                <TextBlock Text="{Binding TargetBankText}" Foreground="Gray" />
            </StackPanel>
        </GroupBox>

        <!-- Progress Panel -->
        <GroupBox Header="Progress" Grid.Row="1" Margin="0,0,0,10">
            <StackPanel Margin="5">
                <TextBlock Text="{Binding StageText}" FontWeight="SemiBold" Margin="0,0,0,5" />
                <ProgressBar Height="20" Value="{Binding ProgressValue}"
                             IsIndeterminate="{Binding IsIndeterminate}" Margin="0,0,0,5" />
                <TextBlock Text="{Binding CounterText}" HorizontalAlignment="Center" />
            </StackPanel>
        </GroupBox>

        <!-- Log -->
        <GroupBox Header="Log" Grid.Row="2" Margin="0,0,0,10">
            <TextBox Text="{Binding LogText, Mode=OneWay}" IsReadOnly="True"
                     VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto"
                     FontFamily="Consolas" FontSize="11"
                     TextChanged="LogTextBox_TextChanged" />
        </GroupBox>

        <!-- Buttons -->
        <StackPanel Grid.Row="3" Orientation="Horizontal" HorizontalAlignment="Right">
            <Button Content="Start" Command="{Binding StartFlashCommand}" Width="90" Margin="5,0"
                    IsEnabled="{Binding CanStart}" />
            <Button Content="Cancel" Command="{Binding CancelFlashCommand}" Width="90" Margin="5,0"
                    IsEnabled="{Binding IsFlashing}" />
            <Button Content="Close" Click="CloseButton_Click" Width="90" Margin="5,0"
                    IsEnabled="{Binding CanClose}" />
        </StackPanel>
    </Grid>
</mah:MetroWindow>
```

- [ ] **Step 2: Create FirmwareUpdateWindow.xaml.cs**

```csharp
using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Views;

public partial class FirmwareUpdateWindow : MetroWindow
{
    public FirmwareUpdateWindow(MainViewModel mainVm)
    {
        InitializeComponent();
        DataContext = new FirmwareUpdateViewModel(mainVm);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.ScrollToEnd();
    }
}
```

- [ ] **Step 3: Verify build**

```bash
cd software && dotnet build CanLinConfig.sln
```

Expected: May have errors for missing converters (`InverseBoolConverter`, `BoolToGreenRedBrushConverter`, `InverseBoolToVisConverter`). These are resolved in Task 9 step 4.

- [ ] **Step 4: Commit**

```bash
git add software/CanLinConfig/Views/FirmwareUpdateWindow.xaml software/CanLinConfig/Views/FirmwareUpdateWindow.xaml.cs
git commit -m "Add FirmwareUpdateWindow dialog UI"
```

---

## Task 9: Wire up UpdateFirmwareCommand and MainWindow button

**Files:**
- Modify: `software/CanLinConfig/ViewModels/MainViewModel.cs`
- Modify: `software/CanLinConfig/Views/MainWindow.xaml`

- [ ] **Step 1: Add UpdateFirmwareCommand to MainViewModel**

Add the following to `software/CanLinConfig/ViewModels/MainViewModel.cs` after the `ReconnectAfterFirmwareUpdate()` method added in Task 5:

```csharp
    [RelayCommand]
    private void UpdateFirmware()
    {
        var window = new Views.FirmwareUpdateWindow(this);
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }
```

Add `using CanLinConfig.Views;` at the top if not already present.

- [ ] **Step 2: Add "Update Firmware" button to MainWindow.xaml**

In `software/CanLinConfig/Views/MainWindow.xaml`, find the "Enter Bootloader" button (line 82) and add a new button after it:

```xml
                <Button Content="Update Firmware" Command="{Binding UpdateFirmwareCommand}" Width="120" Margin="5,0" />
```

Insert this line after the Enter Bootloader button line.

- [ ] **Step 3: Verify build**

```bash
cd software && dotnet build CanLinConfig.sln
```

Expected: 0 errors. The full feature should compile.

- [ ] **Step 4: Check for missing converters**

Check `software/CanLinConfig/Helpers/` and `App.xaml` for existing converters. The XAML uses:
- `InverseBoolConverter` — should already exist
- `BoolToGreenRedBrushConverter` — new, add to `Helpers/Converters.cs`:
- `InverseBoolToVisConverter` — may exist in other views but not in `App.xaml`; add there
- `BoolToVisConverter` — standard `BooleanToVisibility`

```csharp
// Add to Helpers/Converters.cs if not present
public class BoolToGreenRedBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => (bool)value ? Brushes.LightGreen : Brushes.OrangeRed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}
```

Register any new converters in `App.xaml` `<Application.Resources>`.

- [ ] **Step 5: Full build verification**

```bash
cd software && dotnet build CanLinConfig.sln
```

Expected: 0 errors, 0 warnings (or only pre-existing warnings).

- [ ] **Step 6: Commit**

```bash
git add software/CanLinConfig/ViewModels/MainViewModel.cs software/CanLinConfig/Views/MainWindow.xaml
git commit -m "Wire up Update Firmware button and adapter handoff methods"
```

If converters were added:
```bash
git add software/CanLinConfig/Helpers/ software/CanLinConfig/App.xaml
git commit -m "Add missing value converters for firmware update dialog"
```

---

## Task 10: Manual smoke test

- [ ] **Step 1: Launch the config tool**

```bash
cd software && dotnet run --project CanLinConfig
```

Verify: Main window opens, "Update Firmware" button is visible in the bottom bar.

- [ ] **Step 2: Test dialog launch**

Click "Update Firmware". Verify: `FirmwareUpdateWindow` dialog opens as a modal window.

- [ ] **Step 3: Test firmware file selection**

Click "Browse..." and select a valid `.bin` firmware file (e.g., from `firmware/build/`). Verify: version, size, CRC, and signature status are displayed.

Click "Browse..." and select a random non-firmware file. Verify: error message displayed.

- [ ] **Step 4: Test key loading**

Click "Browse..." under HMAC Key and select a 32-byte key file. Verify: "Key loaded" message.

Toggle "Enter manually" and type 64 hex characters. Verify: key accepted.

- [ ] **Step 5: Test dialog close**

Click "Close". Verify: dialog closes cleanly.

- [ ] **Step 6: Commit any fixes from smoke test**

```bash
git add -u
git commit -m "Fix issues found during firmware update smoke test"
```

---

## Task 11: On-target integration test

This requires the board, a CAN adapter, and a valid firmware binary + HMAC key.

- [ ] **Step 1: Full flash test**

1. Connect config tool to the board via PCAN
2. Click "Update Firmware"
3. Select a valid firmware `.bin` file
4. Load the HMAC key
5. Click "Start"
6. Verify: all stages progress (Connecting → Authenticating → Erasing → Flashing → Verifying → Switching Bank → Resetting)
7. Verify: config tool reconnects after reset
8. Verify: firmware version updated

- [ ] **Step 2: Delta update test**

Repeat step 1 with the same firmware file. With "Delta update" checked, verify: "Firmware is identical — no update needed" or only changed sectors are flashed.

- [ ] **Step 3: Cancel test**

Start a flash, click "Cancel" during the flashing stage. Verify: operation aborts cleanly, bootloader returns to IDLE.

- [ ] **Step 4: .dfw test**

If a `.dfw` file is available, repeat step 1 with it. Verify: correct bank image is selected based on active bank.

- [ ] **Step 5: Final commit**

```bash
git add -u
git commit -m "Fix issues found during on-target firmware update testing"
```
