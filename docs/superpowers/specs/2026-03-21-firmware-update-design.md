# Firmware Update via Config Tool — Design Spec

Adds firmware flashing capability to the CanLinConfig Windows config tool, using the existing bootloader protocol libraries (`lib/CanBus.*`).

## Scope

All bootloader protocol features except multi-device scan:

- Binary validation and HMAC signing
- Authentication (HMAC-SHA256 with nonce)
- Dual-bank flashing (auto-detect active bank, flash inactive)
- Delta updates (sector CRC comparison, flash only changed sectors)
- Transfer resume (reconnect and continue interrupted transfers)
- Full progress reporting and cancellation
- Debug message monitoring during flash
- Bootloader config read (version, device identity, diagnostics)

## UI: Modal Dialog Window

### Layout

`FirmwareUpdateWindow` — a modal dialog launched from a new "Update Firmware" button in the `MainWindow` bottom bar (alongside existing "Enter Bootloader").

**Top panel — Setup:**

| Control | Type | Description |
|---------|------|-------------|
| Firmware file | File picker + label | Shows path, version, size, CRC, signature status after selection |
| HMAC key | File picker + hex text box toggle | "Browse..." button, manual hex entry fallback, "Remember path" checkbox |
| Target bank | Read-only label | Auto-populated after bootloader connect: "Bank A -> B" or "B -> A" |
| Delta update | Checkbox (default on) | When checked, compares sector CRCs and only flashes changed sectors |

**Middle panel — Progress:**

| Control | Type | Description |
|---------|------|-------------|
| Stage label | TextBlock | Current step: Connecting / Authenticating / Erasing / Flashing / Verifying / Switching Bank / Resetting |
| Progress bar | ProgressBar | Determinate during erase (sectors) and flash (bytes). Indeterminate for connect/auth/verify/reset |
| Counter label | TextBlock | "Sector 3/12" during erase, "45,056 / 128,000 bytes" during flash |

**Bottom panel — Log & Controls:**

| Control | Type | Description |
|---------|------|-------------|
| Log | TextBox (read-only, scrollable) | Protocol events, debug messages, errors. Auto-scrolls |
| Start button | Button | Disabled until firmware is valid and key is loaded (if auth required). Becomes "Retry" after failure |
| Cancel button | Button | Enabled during flash. Calls `Abort()` then cleans up |
| Close button | Button | Closes dialog. Disabled during active flash (must cancel first) |

### Launch Flow

1. User clicks "Update Firmware" in bottom bar
2. `FirmwareUpdateWindow` opens as modal dialog, receives reference to `MainViewModel` for adapter info
3. Dialog is usable whether or not the config tool is currently connected (user may have already entered bootloader manually)

## Adapter Strategy

The config tool's CAN adapters (`software/CanLinConfig/Adapters/`) use an event-driven interface (`FrameReceived` events, `ConnectAsync`/`DisconnectAsync`). The bootloader protocol lib uses a polling interface (`Receive()` with timeout, `Initialize`/`Shutdown`). Rather than bridging these two paradigms, the firmware update flow uses the lib's own adapter implementations directly.

### Adapter Handoff Sequence

```
1. Record: adapterType, channelName, bitrate from MainViewModel
2. If config tool is connected:
   a. Call MainViewModel.DisconnectForFirmwareUpdate():
      - Sends ENTER_BOOTLOADER via ConfigProtocol (with unlock key)
      - Waits for StatusResult.Success response
      - Tears down: stops diagnostics monitoring, disposes protocol, disconnects + disposes adapter
      - Sets IsConnected = false
   b. Wait 500ms for device reboot (after adapter is fully released)
3. Create lib adapter via AdapterFactory.Create(adapterType)
4. Find matching channel index from lib adapter's ChannelNames[]
   - Iterate ChannelNames, match case-insensitively against config tool's SelectedChannel
   - For Vector XL, use case-insensitive substring match (channel name formats may differ)
5. Initialize lib adapter at 500 kbps (bootloader default bitrate)
6. Run bootloader protocol sequence
7. Shutdown lib adapter
8. If originally connected:
   a. Call MainViewModel.ReconnectAfterFirmwareUpdate():
      - Recreates config tool adapter (same type)
      - Reconnects at original bitrate + channel
      - Re-runs ConfigProtocol handshake
      - Restores IsConnected and diagnostics monitoring
```

### AdapterFactory

Maps config tool adapter type names to lib adapter instances. Returns `CanBus.ICanAdapter` (the lib interface), not the config tool's `CanLinConfig.Adapters.ICanAdapter`:

```csharp
using CanBus;
using CanBus.Adapters;

public static class AdapterFactory
{
    public static CanBus.ICanAdapter Create(string adapterType) => adapterType switch
    {
        "PCAN"      => new PcanService(),
        "Vector XL" => new VectorService(),
        "Kvaser"    => new KvaserService(),
        "SLCAN"     => new SlcanService(),
        _           => throw new ArgumentException($"Unknown adapter: {adapterType}")
    };

    /// <summary>
    /// Find the channel index in the lib adapter matching the config tool's channel name.
    /// Uses case-insensitive comparison; falls back to substring match for Vector XL.
    /// </summary>
    public static int FindChannelIndex(CanBus.ICanAdapter adapter, string channelName)
    {
        var names = adapter.ChannelNames;
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i].Equals(channelName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        // Substring fallback for adapters with different name formats
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i].Contains(channelName, StringComparison.OrdinalIgnoreCase) ||
                channelName.Contains(names[i], StringComparison.OrdinalIgnoreCase))
                return i;
        }
        throw new ArgumentException($"Channel '{channelName}' not found in adapter");
    }
}
```

### Bitrate Handling

- Bootloader always runs at 500 kbps (index 2 for PCAN/Vector/Kvaser, S6 for SLCAN)
- After reset-to-app, config tool reconnects at its original bitrate (which may differ)
- If the user changes the bootloader's bitrate config param, the lib adapter auto-scales timeouts but the CAN bus bitrate itself doesn't change mid-session

## Flash Workflow (FirmwareUpdateService)

### Method Signature

```csharp
public class FirmwareUpdateService
{
    public async Task FlashAsync(
        FlashOptions options,
        IProgress<FlashProgress> progress,
        CancellationToken ct)
}

public record FlashOptions(
    string AdapterType,
    string ChannelName,
    uint OriginalBitrate,
    byte[] Firmware,          // Full binary (header + app data)
    byte[]? HmacKey,          // 32-byte key, null if no auth
    bool DeltaUpdate,         // Try delta if possible
    bool WasConnected         // Whether config tool was connected before
);

public record FlashProgress(
    FlashStage Stage,
    string Message,
    int Current,              // bytes or sectors depending on stage
    int Total,
    bool IsIndeterminate
);

public enum FlashStage
{
    Connecting, Authenticating, QueryingBank, ComparingCRCs,
    Erasing, Flashing, Verifying, SwitchingBank, Resetting,
    Reconnecting, Complete, Failed
}
```

### Threading Model

All `BootloaderProtocol` methods are synchronous (blocking `Receive()` with timeout). `FlashAsync` wraps the entire sequence in `Task.Run()` to avoid blocking the UI thread. Progress is reported via `IProgress<FlashProgress>` which automatically marshals back to the UI thread via `SynchronizationContext`.

### Sequence

```
 1. CreateLibAdapter(adapterType, channelName)
 2. Connect(retry loop, max 10 attempts, 200ms between)
     - If bootloader is in RECEIVING state → offer resume (see Resume section)
 3. If info.AuthRequired:
     a. If no key provided → throw (UI should have prevented this)
     b. protocol.Authenticate(info.Nonce, key)
 4. If FeatureRequirements.IsSupported("DeviceIdentity", info):
     GetDeviceId() → log device identity (UID, flash JEDEC IDs)
 5. Query active bank: ConfigRead(CfgParamActiveBank)
     - targetBank = opposite of active
     - targetAddress = targetBank == 0 ? BankABase : BankBBase
 6. If deltaUpdate:
     a. GetSectorCrcs(targetAddress) — returns null if unsupported
     b. ComputeLocalSectorCrcs(firmware)
     c. Compare → build list of changed sector indices
     d. If no CRCs available or all sectors differ → fall back to full flash
 7. Erase:
     - Delta: erase each changed sector individually
     - Full: Erase(targetAddress, firmware.Length)
     - Report progress via erase callback
 8. SendData:
     - Delta: SendData per changed sector
     - Full: SendData(targetAddress, firmware)
     - Report progress via data callback
 9. Verify:
     - Delta: VerifyApp() — bootloader validates full image (magic + CRC + HMAC)
       Returns (bool Pass, uint DeviceCrc)
     - Full: Verify(targetAddress, firmware) — CRC comparison at target address
       Returns (bool Match, uint DeviceCrc)
10. SwitchBank(targetBank)
11. Reset(ResetModeApp)
12. ShutdownLibAdapter()
```

### Feature Version Checks

The service gates optional protocol features using `FeatureRequirements.IsSupported()`:

| Feature | Min Version | Fallback |
|---------|-------------|----------|
| `DeviceIdentity` | 1.2.1 | Skip device ID logging |
| `TransferResume` | 1.3.3 | Disable resume, always full flash |
| `Diagnostics` | 1.3.11 | Skip CAN bus diagnostics query |

### Resume Support

On step 2, if `Connect()` succeeds and `GetStatus()` reports state=RECEIVING with bytesReceived > 0:

1. Report to UI: "Previous transfer interrupted at {bytesReceived} bytes. Resume or start fresh?"
2. If resume:
   - `QueryResumeOffset()` → page-aligned offset
   - Skip erase (already done)
   - `SendDataResume(targetAddress, firmware, resumeOffset)`
   - Continue from step 9 (verify)
3. If start fresh:
   - `Abort()` to reset state
   - Continue from step 5

The resume decision is surfaced to the UI via a callback on `FirmwareUpdateViewModel`. The VM shows a message and two buttons; the service awaits the user's choice via a `TaskCompletionSource<bool>`.

### Error Recovery

| Error | Action |
|-------|--------|
| `TimeoutException` on Connect | Retry loop (10x). If exhausted: "Device not responding — check CAN connection and power" |
| `ProtocolException(AUTH_REQUIRED)` | Should not happen (checked before start). Show "Authentication required" |
| `ProtocolException(AUTH_FAIL)` | "Authentication failed — check HMAC key" |
| `ProtocolException(CRC_MISMATCH)` on Verify | "Verification failed — firmware may not have written correctly. Retry?" |
| `ProtocolException(FLASH_ERR)` | "Flash hardware error — power cycle the device and retry" |
| `ProtocolException(BAD_SEQ)` | "Sequence error — retrying from beginning" (abort + full restart) |
| `OperationCanceledException` | `Abort()`, clean up adapter, report cancelled |
| Any other exception during flash | `Abort()`, report error with `ProtocolConstants.GetHint()`, offer retry |
| Adapter reconnect failure after success | "Firmware update succeeded! Reconnect manually." (non-fatal) |

All errors call `protocol.Abort()` (wrapped in try/catch) before surfacing to the user.

## Key Management

### Storage

Key file path persisted in `%AppData%/CanLinConfig/firmware-update.json`:

```json
{
    "lastKeyFilePath": "C:\\Keys\\canlinboard.key",
    "lastFirmwarePath": "C:\\Firmware\\canlinboard.bin"
}
```

Loaded on dialog open. Key file re-read from disk each time (never cache key bytes in settings).

### UI Behavior

- On dialog open: if saved key path exists and file is readable, auto-load and show "Key loaded: canlinboard.key (32 bytes)"
- "Browse..." opens file picker (filter: `*.key;*.bin;*.*`)
- "Enter manually" toggle switches to hex text box (validates as 64 hex chars = 32 bytes)
- If `BootloaderInfo.AuthRequired == false` after connect: key fields are optional (grayed label: "Device does not require authentication")
- If `AuthRequired == true` and no key: Start button disabled, message: "HMAC key required"

### Key Validation

- File: must be exactly 32 bytes
- Hex input: must be exactly 64 hex characters (case-insensitive), converted to 32 bytes
- Invalid key → red border + error message, Start button disabled

### Signing

If firmware binary has no signature (`HasSignature == false`) and a key is loaded, the service calls `AppHeader.SignFirmware(firmware, key)` before flashing. This signs the binary in-memory — the original file is not modified.

If the binary already has a signature, it is flashed as-is (the bootloader validates the existing signature).

## Project Structure Changes

### New Files

| File | Purpose |
|------|---------|
| `software/CanLinConfig/Models/AppHeader.cs` | Firmware binary parser/validator. Source: `C:\Projects\Embedded\Bootloader\programmer\CanFlashProgrammer\Models\AppHeader.cs`. Change namespace from `CanFlashProgrammer.Models` to `CanLinConfig.Models`. Key APIs: `TryParse(string filePath)`, `TryParse(byte[])`, `SignFirmware(byte[], byte[])`, `IsValid`, `FullBinary`, `HasSignature`, `VersionString` |
| `software/CanLinConfig/Services/FirmwareUpdateService.cs` | Orchestrates full flash workflow (connect → auth → erase → flash → verify → bank switch → reset) |
| `software/CanLinConfig/Services/AdapterFactory.cs` | Maps config tool adapter type string to lib `ICanAdapter` instance |
| `software/CanLinConfig/Services/FirmwareUpdateSettings.cs` | Loads/saves key path and firmware path from `%AppData%/CanLinConfig/firmware-update.json` |
| `software/CanLinConfig/ViewModels/FirmwareUpdateViewModel.cs` | MVVM view model for the dialog — file selection, key management, progress binding, start/cancel commands |
| `software/CanLinConfig/Views/FirmwareUpdateWindow.xaml` | WPF dialog window with setup panel, progress panel, log panel |

### Modified Files

| File | Change |
|------|--------|
| `software/CanLinConfig.sln` | Add 3 lib projects (paths relative to `.sln`: `..\lib\CanBus.Abstractions\CanBus.Abstractions.csproj`, `..\lib\CanBus.Adapters\CanBus.Adapters.csproj`, `..\lib\CanBus.Protocol\CanBus.Protocol.csproj`) |
| `software/CanLinConfig/CanLinConfig.csproj` | Add `<ProjectReference>` to 3 lib projects (paths: `..\..\lib\CanBus.Abstractions\CanBus.Abstractions.csproj`, etc.) |
| `software/CanLinConfig/ViewModels/MainViewModel.cs` | Add `UpdateFirmwareCommand` (opens dialog), add `DisconnectForFirmwareUpdate()` (enters bootloader + full teardown) and `ReconnectAfterFirmwareUpdate()` (recreate adapter + protocol + handshake) methods |
| `software/CanLinConfig/Views/MainWindow.xaml` | Add "Update Firmware" button in bottom bar |

### NuGet Version Alignment

The config tool uses `Peak.PCANBasic.NET` 4.9.0.942 while `lib/CanBus.Adapters` uses 4.10.1.968. To avoid version conflicts, upgrade the config tool's PCAN NuGet reference to match 4.10.1.968 before adding the lib project references. Verify the config tool's `PcanAdapter` still works with the newer version (the API surface is stable between 4.9 and 4.10).

### Namespace Mapping

The lib uses `CanBus`, `CanBus.Adapters`, `CanBus.Protocol` namespaces. The config tool uses `CanLinConfig.*`. No conflicts — they coexist cleanly since the lib's `ICanAdapter` is `CanBus.ICanAdapter` and the config tool's is `CanLinConfig.Adapters.ICanAdapter`.

## Debug & Diagnostics Integration

During the flash workflow, the service subscribes to the lib adapter's events:

- `adapter.DebugMessageReceived` → parse via `DebugMessage.TryParse()`, append to log
- `adapter.FrameTraced` → append raw CAN trace to log (if verbose mode enabled)
- `adapter.HeartbeatReceived` → update "device alive" indicator

After connect, the service logs:
- Bootloader version and protocol version
- Device identity (UID, flash JEDEC IDs) if supported
- CAN bus diagnostics (parse errors, RX overflows) if supported
- Active bank and installed firmware CRC

## Testing Strategy

### Manual Test Cases

| # | Test | Expected |
|---|------|----------|
| 1 | Select valid .bin file | Version, size, CRC displayed. Signature status shown |
| 2 | Select invalid file (wrong magic) | Error: "Not a valid firmware file" |
| 3 | Select corrupted file (bad CRC) | Error: "CRC mismatch — file may be corrupted" |
| 4 | Load valid 32-byte key file | "Key loaded: filename.key (32 bytes)" |
| 5 | Load wrong-size key file | Error: "Key must be exactly 32 bytes" |
| 6 | Enter valid 64-char hex key | Key accepted, hex converted to bytes |
| 7 | Start without key when auth required | Start button disabled, message shown |
| 8 | Full flash (no existing firmware) | Erase → Flash → Verify → Bank switch → Reset. All stages progress correctly |
| 9 | Delta flash (few sectors changed) | Only changed sectors erased/flashed. Counter shows e.g. "3/48 sectors changed" |
| 10 | Delta fallback (no CRCs available) | Falls back to full flash, log shows reason |
| 11 | Cancel during flash | Abort sent, bootloader returns to IDLE, dialog shows "Cancelled" |
| 12 | Resume interrupted transfer | On connect, resume offered. Resumes from correct offset |
| 13 | Auth failure (wrong key) | "Authentication failed" error, retry offered |
| 14 | Verify failure (CRC mismatch) | "Verification failed" error, retry offered |
| 15 | Config tool reconnect after success | Config tool reconnects, firmware version updated |
| 16 | Reconnect failure after success | "Update succeeded! Reconnect manually." (non-fatal) |
| 17 | Update from disconnected state | Dialog works without prior config tool connection |
| 18 | Unsigned firmware + key loaded | Firmware signed in-memory before flash |

### Unit Test Targets (future)

- `AppHeader.TryParse` — valid, invalid magic, bad CRC, oversized, undersized
- `AppHeader.SignFirmware` — round-trip: sign → parse → verify signature present
- `AdapterFactory.Create` — all adapter types map correctly, unknown throws
- `FirmwareUpdateSettings` — save/load round-trip
