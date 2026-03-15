# Test Guide — Windows Config Tool (CanLinConfig)

This guide covers testing the CanLinConfig WPF application.

---

## Prerequisites

| Requirement | Detail |
|-------------|--------|
| **.NET 8 SDK** | `dotnet --version` must show 8.x |
| **Firmware** | Phases 0-6 firmware with Phase 8 protocol extensions (BULK_READ, device sizeof query) |
| **CAN adapter** | PCAN-USB, Vector XL, or SLCAN device connected to board CAN1 at 500 kbps |
| **Board** | Powered, running current firmware |

### Adapter-Specific Requirements

| Adapter | Requirement |
|---------|-------------|
| PCAN | Peak driver installed, PCAN-USB connected |
| Vector XL | Vector XL Driver Library installed (`vxlapi64.dll`) |
| Kvaser | Kvaser CANlib SDK installed (`canlib32.dll`) — untested |
| SLCAN | SLCAN-compatible USB device (CANable, USBtin, etc.) |

---

## Step 0: Build

```bash
cd software
dotnet build CanLinConfig.sln
```

**Expected:** `0 Warning(s), 0 Error(s)`

---

## Step 1: Launch

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

## Step 2: Connect

1. Select adapter (PCAN, Vector XL, or SLCAN)
2. Select channel from the dropdown (auto-populated from adapter)
3. Bitrate: **500000**
4. Click **Connect**

### Expected Results

| Indicator | Value |
|-----------|-------|
| Status LED | Green circle |
| Connection text | `Connected - FW v0.1.0` |
| Status bar | `Connected to device (FW v0.1.0, config size=XXXX, rules=0)` |

---

## Step 3: Read All Parameters

1. Click **Read All**
2. Status bar: "Read All complete"
3. Verify defaults on each tab:

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

### Routing Tab

- Rule list should be empty (0 rules)

---

## Step 4: Write + Save Round-Trip

1. Set CAN2 Enabled = ON, CAN2 Termination = ON
2. Set Diagnostics Interval = 2000 ms
3. Click **Write All** then **Save NVM**
4. Click **Read All** — verify changes persisted
5. Power-cycle the board, reconnect, Read All — verify settings survived

---

## Step 5: Live Diagnostics

1. Go to **Live Diagnostics** tab
2. Wait 2-3 seconds

| Panel | Expected |
|-------|----------|
| System State | OK |
| Uptime | Counting up |
| MCU Temp | 20-50 C |
| CAN1 RX | Incrementing |
| Frame Monitor | Scrolling frames (0x7F0, 0x7F1, 0x7F2, 0x7F4) |

---

## Step 6: Config File Export / Import

1. Click **Export File** — save as `test_config.json`
2. Verify JSON structure: `can[]`, `lin[]`, `diag`, `routing[]` sections
3. Change a setting, click **Import File** — verify revert
4. Routing rules with `ProfileTag` should include `profile_tag` in JSON

---

## Step 7: Routing Rules (Bulk Transfer)

1. **Routing** tab > Add Rule: Src Bus=CAN1, Src ID=100, Dst Bus=CAN1, Dst ID=200
2. Add byte mapping: Src Byte=0, Dst Byte=0, Mask=FF
3. Click **Write All** then **Read All** — verify round-trip
4. Profile column shows ProfileTag for profile-generated rules

---

## Step 8: LIN Schedule (Bulk Transfer)

1. **LIN** tab > LIN1 > Mode: Master, Enabled: ON
2. Add schedule entries (ID, DLC, Direction, Delay)
3. Click **Write All** then **Read All** — verify round-trip

---

## Step 9: Profiles

### Apply Profile

1. **Profiles** tab — two profiles listed: WDA Wiper, CWA400 Pump
2. Select WDA, assign to LIN1, click **Apply Profile**
3. Status bar: "Profile 'Bosch WDA LIN Wiper Motor' applied to LIN1 with 2 routing rules"
4. **Routing** tab: 2 rules with ProfileTag "wda-wiper:0"
5. **LIN** tab: LIN1 = Master, 19200 baud, schedule entries populated

### Parameter Controls

1. After apply: "Device Parameters" section visible
2. WDA: "Wiper Mode" dropdown (Off/Slow/Fast/Interval), "Interval Time" slider (0-255)
3. Click **Send Control Frame** — status bar shows sent CAN frame

### Import / Export Profile

1. Click **Export** — saves selected profile as JSON
2. Click **Import** — loads external profile JSON, validates `name`/`id` fields
3. Duplicate detection prompts for replacement

### Re-apply / Multi-profile

- Re-applying to different channel removes old rules, adds new ones
- Two profiles on different channels coexist (4 rules total)

---

## Step 10: Adapter Tests

### PCAN
Standard adapter — all tests above apply.

### Vector XL
1. Select **Vector XL** adapter — channels show real hardware names
2. Connect, Read All, Write All — same behavior as PCAN
3. Disconnect + reconnect works cleanly
4. Without Vector driver installed: no crash, empty channel list

### SLCAN
1. Select **SLCAN**, choose COM port
2. Connect at 500 kbps — verify Read All works

---

## Step 11: Disconnect / Reconnect

1. Click **Disconnect** — status = "Disconnected"
2. Click **Connect** — should reconnect normally

---

## Step 12: Enter Bootloader (Destructive)

**Warning:** Reboots board into bootloader mode. Re-flash firmware to continue.

1. Click **Enter Bootloader**, confirm dialog
2. Status: "Device rebooting to bootloader", auto-disconnect

---

## Test Summary Checklist

| # | Test | Status |
|---|------|--------|
| 0 | Config tool builds (0 warnings, 0 errors) | |
| 1 | App launches with correct UI layout | |
| 2 | CAN adapter connect + FW handshake | |
| 3 | Read All — default values correct | |
| 4 | Write All + Save NVM + power-cycle persistence | |
| 5 | Live Diagnostics dashboard + frame monitor | |
| 6 | Config file export/import round-trip | |
| 7 | Routing rules bulk write + read back | |
| 8 | LIN schedule bulk write + read back | |
| 9 | Profile apply — LIN config + routing rules generated | |
| 10 | Profile parameter controls + Send Control Frame | |
| 11 | Profile import/export | |
| 12 | Vector XL adapter — connect, TX, RX, reconnect | |
| 13 | Disconnect / reconnect | |
| 14 | Enter Bootloader (destructive) | |

---

## Known Limitations

| Item | Detail |
|------|--------|
| **Kvaser adapter** | Full implementation skeleton but untested — requires Kvaser CANlib SDK |
| **Dirty tracking** | No unsaved-changes warning on close |
| **Profile JSON** | LIN IDs and data are placeholder values — fill in from real device datasheets |
| **BULK_START CRC** | 24-bit (lower 3 bytes of CRC32). Incompatible with pre-Phase 8 firmware |
