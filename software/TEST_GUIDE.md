# Test Guide — Phase 8: Windows Config Tool (CanLinConfig)

This guide covers testing the CanLinConfig WPF application and the
firmware protocol extensions added in Phase 8a.

---

## Prerequisites

| Requirement | Detail |
|-------------|--------|
| **.NET 8 SDK** | `dotnet --version` must show 8.x |
| **Firmware** | Phases 0-6 firmware **rebuilt with Phase 8a extensions** (new BULK_START format + BULK_READ) |
| **PCAN-USB** | Connected to board CAN1 at 500 kbps. Peak driver installed |
| **Board** | Powered, running updated firmware |

### Optional (for additional adapter tests)

| Adapter | Requirement |
|---------|-------------|
| Kvaser | Kvaser CANlib SDK installed (`canlib32.dll` in system PATH) |
| Vector XL | Vector XL Driver Library installed (`vxlapi64.dll`) — **TX is stubbed** |
| SLCAN | SLCAN-compatible USB device (CANable, USBtin, etc.) |

---

## Step 0: Flash Updated Firmware

The config protocol changed in Phase 8a — `BULK_START` now includes a `sub`
byte and the CRC is 24-bit. You **must** flash the new firmware before testing.

```bash
cd firmware
cmake -B build -S . -G Ninja
cmake --build build
# Flash build/CanLinBoard.bin via bootloader
```

Verify the build:

```bash
python tests/phase0/test_build.py
# Expected: 18/18 PASS
```

---

## Step 1: Build the Config Tool

```bash
cd software
dotnet build CanLinConfig.sln
```

**Expected:** `0 Warning(s), 0 Error(s)`

For a Release build:

```bash
dotnet build CanLinConfig.sln --configuration Release
```

---

## Step 2: Launch

```bash
dotnet run --project CanLinConfig/CanLinConfig.csproj
```

### Visual Checks

| Item | Expected |
|------|----------|
| Window theme | MahApps.Metro dark blue |
| Tab bar | 6 tabs: CAN, LIN, Routing, Diagnostics Settings, Live Diagnostics, Profiles |
| Connection bar | Adapter dropdown (PCAN/Kvaser/Vector XL/SLCAN), Channel, Bitrate, Connect button |
| Bottom bar | Read All, Write All, Save NVM, Load Defaults, Enter Bootloader, Export File, Import File |
| Status bar | "Ready" at bottom-left |

---

## Step 3: PCAN Connection

1. Select adapter: **PCAN**
2. Select channel: **PCAN_USBBUS1** (should auto-populate)
3. Bitrate: **500000**
4. Click **Connect**

### Expected Results

| Indicator | Value |
|-----------|-------|
| Status LED | Green circle |
| Connection text | `Connected - FW v0.1.0` |
| Status bar | `Connected to device (FW v0.1.0, config size=XXXX, rules=0)` |

### Troubleshooting

| Symptom | Cause / Fix |
|---------|-------------|
| No channels listed | Peak driver not installed, or PCAN-USB not plugged in |
| "Connection failed" | Wrong channel selected, or adapter in use by another app |
| "No device response" | Board not running updated firmware, or wrong bitrate |

---

## Step 4: Read All Parameters

1. Click **Read All**
2. Status bar should show "Read All complete"
3. Switch through each config tab and verify default values:

### CAN Tab

| Field | CAN1 Default | CAN2 Default |
|-------|-------------|-------------|
| Bitrate | 500000 | 500000 |
| Termination | OFF | OFF |
| Enabled | (always on, grayed) | OFF |

### LIN Tab (all 4 channels)

| Field | Default |
|-------|---------|
| Enabled | OFF |
| Mode | Disabled |
| Baud Rate | 19200 |
| Schedule | Empty (hidden until Master mode) |

### Diagnostics Settings Tab

| Field | Default |
|-------|---------|
| Enabled | ON |
| CAN ID | 0x7F0 |
| Interval | 1000 ms |
| Bus | CAN1 |
| CAN Watchdog | 0 (disabled) |
| LIN Watchdog | 0 (disabled) |

### Routing Tab

- Rule list should be empty (0 rules)

---

## Step 5: Write + Save Round-Trip

Test that parameter changes persist across power cycles.

### Procedure

1. **CAN tab:** Set CAN2 Enabled = ON, CAN2 Termination = ON
2. **Diag tab:** Set Interval = 2000 ms
3. Click **Write All** — status bar: "Write All complete"
4. Click **Save NVM** — status bar: "Saved to NVM"
5. Click **Read All** — verify CAN2 enabled + termination, interval = 2000
6. **Power-cycle the board** (unplug/replug)
7. Click **Connect** again
8. Click **Read All** — verify settings persisted

### Pass Criteria

All changed values match after power cycle.

---

## Step 6: Live Diagnostics

1. Go to **Live Diagnostics** tab
2. Wait 2-3 seconds for heartbeat frames to arrive

### Dashboard Checks

| Panel | Expected |
|-------|----------|
| System State | OK |
| Uptime | Counting up |
| MCU Temp | 20-50 C (room temp range) |
| Reset Reason | Power On |
| CAN1 RX | Incrementing (from diag heartbeat) |
| Heap Free | > 0 KB |
| Min Stack | > 0 words |
| WDT Mask | 0x00 (no timeouts) |
| Crash Report | None |

### Frame Monitor Checks

1. Verify scrolling CAN frames visible (0x7F0, 0x7F1, 0x7F2, 0x7F4)
2. Type `7F0` in the ID filter field — only 0x7F0 frames shown
3. Click **Pause** — frames stop scrolling
4. Click **Resume** — frames resume
5. Click **Clear** — log emptied

---

## Step 7: Config File Export / Import

1. Click **Export File** — save as `test_config.json`
2. Open the file in a text editor — verify JSON structure:
   - `can[]`, `lin[]`, `diag`, `routing[]` sections
   - `fw_version`, `export_time` metadata
3. Change a setting in the UI (e.g., CAN2 termination OFF → ON)
4. Click **Import File** — load the saved JSON
5. Verify the UI reverted to the exported values

### Pass Criteria

Settings round-trip through JSON file correctly. File is human-readable.

---

## Step 8: Routing Rules (Bulk Transfer)

### Write Rules

1. Go to **Routing** tab
2. Click **Add Rule**
3. Configure:
   - Enabled: checked
   - Src Bus: 0 (CAN1)
   - Src ID: `100` (type in hex)
   - Src Mask: `7FF`
   - Dst Bus: 0 (CAN1)
   - Dst ID: `200`
4. Click **Add Rule** again for a second rule:
   - Src Bus: 0, Src ID: `101`, Dst Bus: 1 (CAN2), Dst ID: `301`
5. Click **Write All** — status bar: "Write All complete"

### Read Back

6. Click **Read All**
7. Verify both rules appear in the grid with correct values

### Byte Mapping Sub-Editor

8. Select a rule, click **Add Mapping**
9. Set: Src Byte=0, Dst Byte=0, Mask=FF, Shift=0, Offset=0
10. Click **Write All** then **Read All** — mapping should persist

### Important Note

Routing rule bulk serialization depends on `sizeof(routing_rule_t)` matching
between the config tool and the ARM target. If rules don't round-trip:

```c
// Add to any test firmware main():
printf("sizeof(routing_rule_t) = %u\n", sizeof(routing_rule_t));
// Or report via CAN test frame
```

Then update `RoutingRule.cs:Serialize()` to match the actual size.

---

## Step 9: LIN Schedule (Bulk Transfer)

1. Go to **LIN** tab, select **LIN1**
2. Set Mode: **Master**, Enabled: **ON**
3. Click **Add** to add schedule entries:
   - Entry 1: ID=21 (hex), DLC=2, Direction=1 (Publish), Delay=20ms
   - Entry 2: ID=22 (hex), DLC=4, Direction=0 (Subscribe), Delay=20ms
4. Click **Write All** — schedule sent via BULK_WRITE to device
5. Click **Read All** — schedule entries should re-appear in the grid
6. Verify entry count, IDs, DLC, and delays match

---

## Step 10: Profiles

1. Go to **Profiles** tab
2. Two placeholder profiles should be listed:
   - "Bosch WDA LIN Wiper Motor"
   - "Pierburg CWA400 LIN Coolant Pump"
3. Select a profile — detail panel shows description, LIN mode, baudrate
4. Set "Assign to LIN Channel" dropdown to LIN1
5. Click **Apply Profile**
6. Go to **LIN** tab — verify LIN1 is now Master, 19200 baud, with schedule entries
7. Click **Save NVM** to persist

---

## Step 11: Disconnect / Reconnect

1. Click **Disconnect**
2. Verify: green LED → gray, status = "Disconnected"
3. Verify: all buttons except Connect grayed out
4. Click **Connect** again — should reconnect normally

---

## Step 12: Enter Bootloader (Destructive)

**Warning:** This reboots the board into bootloader mode. You will need to
re-flash firmware to continue testing.

1. Click **Enter Bootloader**
2. Confirm the dialog
3. Expected: status = "Device rebooting to bootloader", auto-disconnect
4. Board should now respond to bootloader CAN commands (0x700/0x701)

---

## Additional Adapter Tests (Optional)

### SLCAN

1. Connect a SLCAN device (e.g., CANable) to USB
2. Select adapter: **SLCAN**
3. Select channel: **COMx@115200** (matching your device)
4. Set bitrate: **500000**, click **Connect**
5. Click **Read All** — verify parameters read correctly

### Kvaser

1. Install Kvaser CANlib SDK
2. Select adapter: **Kvaser**
3. Select channel: **Kvaser_CH0**
4. Click **Connect** — verify connection

### Vector XL

Vector XL adapter has a **stub TX implementation** — it can connect and receive
but cannot send frames. Full implementation requires proper `xl_can_msg_t`
struct marshaling.

---

## Test Summary Checklist

| # | Test | Status |
|---|------|--------|
| 0 | Firmware build (18/18 Phase 0) | |
| 1 | Config tool builds (0 warnings, 0 errors) | |
| 2 | App launches with correct UI layout | |
| 3 | PCAN connect + FW handshake | |
| 4 | Read All — default values correct | |
| 5 | Write All + Save NVM + power-cycle persistence | |
| 6 | Live Diagnostics dashboard + frame monitor | |
| 7 | Config file export/import round-trip | |
| 8 | Routing rules bulk write + read back | |
| 9 | LIN schedule bulk write + read back | |
| 10 | Profile apply → LIN config populated | |
| 11 | Disconnect / reconnect | |
| 12 | Enter Bootloader (destructive) | |

---

## Known Limitations

| Item | Detail |
|------|--------|
| **sizeof(routing_rule_t)** | Must be verified on ARM target. If mismatch, bulk read/write of routing rules will corrupt data. Print `sizeof` and update `RoutingRule.cs` |
| **Vector XL adapter** | TX stubbed — `Send()` returns false. Needs full struct marshaling for `xlCanTransmit` |
| **Profile JSON** | Placeholder LIN IDs/data — fill in real values from device datasheets |
| **Dirty tracking** | Not yet implemented — no unsaved-changes warning on close |
| **BULK_START CRC** | Now 24-bit (lower 3 bytes of CRC32). Previous firmware (pre-8a) used 32-bit CRC at different byte offsets — incompatible |
