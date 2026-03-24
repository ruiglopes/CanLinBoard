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

---

## Bus Monitor Tests (Plan 1 — No Hardware Required)

### Automated Unit Tests

```bash
cd software
dotnet test CanLinConfig.Tests -v normal
```

**Expected: 128 tests passing**

| Test Class | Count | What it covers |
|------------|-------|----------------|
| SignalExtractorTests | 9 | Intel/Motorola byte order, signed/unsigned, factor/offset, nibble, boolean |
| DatabaseManagerTests | 6 | DBC load, signal cache, message name lookup, frame decoding, remove |
| BusDataServiceTests | 6 | Frame events, signal decode events, CAN1 wrapping, history buffer, cap |
| TracePanelViewModelTests | 4 | Add frame, max entries cap, pause, clear |
| IntegrationTests | 2 | End-to-end pipeline: frame → trace + signals; unknown message handling |
| UnitTest1 | 1 | Placeholder (template) |

---

### Bus Monitor UI Walkthrough (No Connection Needed)

#### BM-1: Tab exists and layout is correct

1. Launch config tool: `dotnet run --project CanLinConfig/CanLinConfig.csproj`
2. Confirm tab bar shows: CAN | LIN | Routing | Diagnostics Settings | **Bus Monitor** | Live Diagnostics | Profiles
3. Click "Bus Monitor"
4. Verify layout:

| Area | Expected |
|------|----------|
| Top bar | CAN1 DB: (none) [...] [X] — CAN2 DB: (none) [...] [X] |
| Top-left panel | Trace: empty DataGrid (Time, Bus, ID, DLC, Data, Message) |
| Top-right panel | Signal: empty DataGrid (Signal, Value, Unit, Raw, Min, Max, Bus, Last Update) |
| Bottom panel | Graph: ScottPlot chart with Pause/Clear/Window toolbar |
| Splitters | Vertical between trace/signals, horizontal above graph — both draggable |

#### BM-2: DBC file assignment

1. Click "..." next to CAN1 DB
2. Browse to any `.dbc` file (e.g., `docs/CanLinBoard.dbc`)
3. Filename appears next to "CAN1 DB:"
4. Click "X" → resets to "(none)"
5. Repeat for CAN2 DB

#### BM-3: Trace panel controls

1. Click Pause → button text changes to "Resume"
2. Click Resume → button text changes to "Pause"
3. Click Clear (no crash when empty)
4. Type "7F0" in ID filter field (no crash, field accepts input)
5. Select "CAN1" from bus dropdown, then "All"

#### BM-4: Signal panel context menu

1. Right-click on the empty signal DataGrid
2. Context menu appears with "Add to Graph"
3. Click "Add to Graph" (no crash — nothing happens since no signal selected)

#### BM-5: Graph panel controls

1. Click Pause/Resume on graph toolbar
2. Click Clear (no crash when empty)
3. Change window dropdown: 10s → 30s → 60s → 5m (no crash)

---

### Bus Monitor Live Test (With CAN Adapter)

#### BM-6: Live frame capture

1. Connect to a CAN adapter at 500 kbps
2. Switch to Bus Monitor tab
3. If CAN traffic present: frames appear in Trace panel
4. Verify columns: Time (HH:mm:ss.fff), Bus (CAN1), ID (0xNNN), DLC, Data (hex bytes)

#### BM-7: DBC signal decoding

1. While connected, assign `docs/CanLinBoard.dbc` as CAN1 DB
2. Diagnostics heartbeat frames (0x7F0) should show message name in the "Message" column
3. Signal panel populates with decoded values (uptime, system state, MCU temp, etc.)
4. Values update in real-time as new frames arrive

#### BM-8: Signal graphing

1. In Signal panel, right-click a signal → "Add to Graph"
2. Graph shows a time-series line
3. Add a second signal — both visible with legend
4. Pause graph → line stops, Resume → catches up
5. Clear → graph empties

#### BM-9: Filtering

1. Type a hex ID in the trace ID filter (e.g., "7F1") → only matching frames shown
2. Clear filter → all frames return
3. Select bus filter "CAN1" → same effect (all traffic is CAN1 for now)

#### BM-10: Pause/resume trace

1. Pause trace → no new frames appear
2. Resume → frames resume appearing

---

### Simulated Traffic Test (No Hardware At All)

For testing the full pipeline without any CAN hardware, temporarily add a test frame injection button:

1. Add to `BusMonitorViewModel.cs`:
```csharp
[RelayCommand]
private void InjectTestFrame()
{
    byte[] data = [0xE8, 0x03, 200, 0x01, 0, 0, 0, 0];
    var frame = new Adapters.CanFrame(256, data);
    _busDataService.OnCanFrame(frame);
}
```

2. Add to `BusMonitorView.xaml` in the database bar StackPanel:
```xml
<Button Content="Test Frame" Command="{Binding InjectTestFrameCommand}" Margin="15,0,0,0"/>
```

3. Launch the app, go to Bus Monitor tab
4. Assign `software/CanLinConfig.Tests/TestData/test.dbc` as CAN1 DB
5. Click "Test Frame" repeatedly

| What to verify | Expected |
|----------------|----------|
| Trace entry | ID=0x100, Bus=CAN1, DLC=8, Message=EngineData |
| EngineRPM signal | Value=250.0, Unit=rpm, Raw=1000 |
| CoolantTemp signal | Value=160.0, Unit=C, Raw=200 |
| EngineOn signal | Value=1.0, Raw=1 |
| Graph (after adding a signal) | Points appearing on each click |

**Remove the test button before merging to main.**

---

### Bus Monitor Test Checklist

| # | Test | Hardware | Status |
|---|------|----------|--------|
| — | Unit tests (128 total) | None | PASS |
| BM-1 | Tab exists, layout correct | None | PASS |
| BM-2 | DBC file assignment | None | PASS |
| BM-3 | Trace panel controls | None | PASS |
| BM-4 | Signal panel context menu | None | PASS |
| BM-5 | Graph panel controls | None | PASS |
| BM-6 | Live frame capture | CAN adapter | PASS |
| BM-7 | DBC signal decoding | CAN adapter | PASS |
| BM-8 | Signal graphing | CAN adapter | PASS |
| BM-9 | ID/bus filtering | CAN adapter | PASS |
| BM-10 | Pause/resume trace | CAN adapter | PASS |
| SIM | Simulated traffic (inject button) | None | PASS |

---

## Project System Tests (Plan 2 — No Hardware Required)

### Automated Unit Tests

Included in the 128-test suite above. Specific project system tests:

| Test Class | Count | What it covers |
|------------|-------|----------------|
| AppSettingsTests | 3 | Load defaults, save/load round-trip, recent projects cap + dedup |
| ProjectServiceTests | 5 | Create from state, save/open round-trip, DBC embedding, timestamp update, missing file error |
| ProjectIntegrationTests | 3 | Full lifecycle (create/save/close/reopen + DBC intact), ZIP validity, AppSettings tracking |

---

### Project System UI Walkthrough (No Connection Needed)

#### PJ-1: File menu exists

1. Launch config tool
2. Verify menu bar at the top: **File**
3. Click File — dropdown shows: New Project, Open Project..., Save Project, Save Project As..., Close Project
4. Verify keyboard shortcuts shown: Ctrl+N, Ctrl+O, Ctrl+S

#### PJ-2: New Project

1. File > New Project (or Ctrl+N)
2. Window title changes to "CanLinConfig — New Project *" (asterisk = unsaved)
3. Status bar shows "New project created"

#### PJ-3: Save Project As

1. After creating a new project, File > Save Project As...
2. Save dialog appears with `.clpkg` filter
3. Save as `test-project.clpkg`
4. Window title changes to "CanLinConfig — New Project" (no asterisk)
5. Status bar shows "Project saved as test-project.clpkg"
6. Verify the `.clpkg` file exists on disk

#### PJ-4: DBC embedding in project

1. Go to Bus Monitor tab, assign a DBC file to CAN1
2. File > Save Project (Ctrl+S)
3. Close the app, relaunch
4. File > Open Project... → open the saved `.clpkg`
5. Bus Monitor tab should show the CAN1 DB filename restored
6. The DBC is embedded inside the .clpkg — works even if the original DBC file is moved/deleted

#### PJ-5: Open Project

1. File > Open Project... (or Ctrl+O)
2. Browse to a previously saved `.clpkg` file
3. Window title shows project name
4. Status bar shows "Opened project: ..."
5. Connection settings (adapter, channel, bitrate) restored from project
6. DBC assignments restored in Bus Monitor tab

#### PJ-6: Close Project

1. File > Close Project
2. Window title resets to "CanLinConfig"
3. DBC assignments cleared in Bus Monitor tab
4. Status bar shows "Project closed"

#### PJ-7: Unsaved changes prompt

1. Create or open a project
2. Change something (e.g., assign a DBC file)
3. Window title should show "*" (dirty indicator)
4. Try to close the window (X button) or File > Close Project
5. Dialog appears: "Save changes to the current project?" with Yes / No / Cancel
6. Click Cancel → window stays open, project stays loaded
7. Click No → project closed without saving
8. Click Yes → project saved, then closed

#### PJ-8: Keyboard shortcuts

1. Ctrl+N → New Project dialog/action
2. Ctrl+O → Open Project file dialog
3. Ctrl+S → Save Project (Save As if never saved)

#### PJ-9: Auto-load last project (optional)

To test this, manually edit `%AppData%/CanLinConfig/settings.json`:
```json
{
  "load_last_project": true,
  "last_project_path": "C:\\path\\to\\your\\project.clpkg"
}
```
Relaunch the app — the project should auto-load on startup.

#### PJ-10: .clpkg is a valid ZIP

1. Rename a saved `.clpkg` file to `.zip`
2. Open with Windows Explorer or 7-Zip
3. Verify contents: `project.json` at root, `databases/` folder with DBC files (if any were assigned)
4. Open `project.json` — verify it contains readable JSON with connection, databases, bus_monitor sections

---

### Project System Test Checklist

| # | Test | Hardware | Status |
|---|------|----------|--------|
| — | Unit tests (11 project-specific) | None | PASS |
| PJ-1 | File menu exists | None | PASS |
| PJ-2 | New Project | None | PASS |
| PJ-3 | Save Project As | None | PASS |
| PJ-4 | DBC embedding in project | None | PASS |
| PJ-5 | Open Project | None | PASS |
| PJ-6 | Close Project | None | PASS |
| PJ-7 | Unsaved changes prompt | None | PASS |
| PJ-8 | Keyboard shortcuts | None | PASS |
| PJ-9 | Auto-load last project | None | PASS |
| PJ-10 | .clpkg is a valid ZIP | None | PASS |

---

## LDF Integration Tests (Plan 3 — No Hardware Required)

### Automated Unit Tests

Included in the test suite. LDF-specific tests:

| Test Class | Count | What it covers |
|------------|-------|----------------|
| LdfIntegrationTests | 6 | LDF load, signal conversion, frame decoding, auto-detect, remove, encoding defaults |

### LDF Manual Tests

#### LDF-1: Assign LDF to LIN bus

1. Launch config tool, go to Bus Monitor tab
2. Verify LIN1-4 database assignment row visible
3. Click "..." next to LIN1
4. File dialog shows "Database Files (*.dbc;*.ldf)" filter
5. Select an LDF file → filename appears next to "LIN1:"
6. Click "X" → resets to "(none)"

#### LDF-2: LDF in project save/load

1. Assign an LDF file to LIN1
2. File > Save Project As → save as test.clpkg
3. Close project, reopen → LIN1 database assignment restored
4. Rename/delete the original LDF file → reopen project → still works (embedded copy)

### LDF Test Checklist

| # | Test | Hardware | Status |
|---|------|----------|--------|
| — | Unit tests (6 LDF-specific) | None | PASS |
| LDF-1 | Assign LDF to LIN bus | None | PASS |
| LDF-2 | LDF in project save/load | None | PASS |

---

## Export Format Tests (Plan 6 — No Hardware Required)

### Automated Unit Tests

| Test Class | Count | What it covers |
|------------|-------|----------------|
| CsvExporterTests | 4 | Header/rows, signal columns, empty frames, file extension |
| AscExporterTests | 4 | ASC header, frame line format, bus-to-channel mapping, file extension |
| BlfExporterTests | 4 | BLF signature, file size, multi-frame scaling, file extension |
| ExportIntegrationTests | 4 | All exporters with mixed bus frames, CSV with LDF signal decode |

### Export Manual Tests

#### EXP-1: Export CSV from Bus Monitor

1. Capture some frames in the Bus Monitor (live or simulated)
2. Click "Export" button
3. Select CSV format, save
4. Open in Excel/text editor — verify header row, data rows with timestamps, bus, ID, data
5. If DBC loaded: signal columns should be present with physical values

#### EXP-2: Export ASC from Bus Monitor

1. Capture frames, click Export, select ASC format
2. Open in text editor — verify ASC header (date, base hex, timestamps absolute)
3. Verify frame lines: timestamp, channel, ID, Rx, d, DLC, data
4. If available: open in PCAN-View or CANalyzer to validate

#### EXP-3: Export BLF from Bus Monitor

1. Capture frames, click Export, select BLF format
2. Open in PCAN-View or CANalyzer — verify frames are readable
3. Verify timestamps and channel mapping are correct

#### EXP-4: Export with no frames

1. Click Export with empty trace → info dialog "No frames to export"

### Export Test Checklist

| # | Test | Hardware | Status |
|---|------|----------|--------|
| — | Unit tests (16 export-specific) | None | PASS |
| EXP-1 | CSV export | None | PASS |
| EXP-2 | ASC export | None | PASS |
| EXP-3 | BLF export validation | PCAN-View/CANalyzer | PASS |
| EXP-4 | Export with no frames | None | PASS |

---

## Instrument Panel Tests (Plan 7 — No Hardware Required)

### Automated Unit Tests

| Test Class | Count | What it covers |
|------------|-------|----------------|
| InstrumentPanelViewModelTests | 14 | Add/remove widget, duplicate prevention, signal routing, normalized value, layout round-trip, BitPanel model round-trip, bit decomposition, auto-layout |

### Instrument Panel Manual Tests

#### INS-1: Add signal to instrument panel

1. Go to Bus Monitor tab, use simulated traffic or live connection
2. Load a DBC file, wait for signals to appear in Signal panel
3. Right-click a signal → "Add to Instrument Panel"
4. Switch to Instruments tab (bottom section) → widget appears

#### INS-2: Widget type selector submenu

1. Add a signal to instrument panel
2. Right-click the widget → "Change Type" submenu appears
3. Submenu lists 6 types with checkmarks: Numeric, Bar, Gauge, Boolean, BitPanel, Enum
4. Select a type → widget changes immediately
5. Current type shows a checkmark in the submenu
6. Each type renders correctly with live updating values

#### INS-3: Remove widget

1. Right-click a widget → "Remove"
2. Widget disappears from panel

#### INS-4: Free-position drag and snap

1. Add 3-4 different signals to instrument panel
2. Widgets appear on a Canvas (free-position layout)
3. Drag a widget — it moves freely with the mouse
4. Move near the edge of another widget or the panel edge — snap guidelines (blue lines) appear at 8px proximity
5. Release — widget snaps to the guideline position
6. Press Escape while dragging — widget returns to its original position
7. Dragged widget appears on top (z-order promotion)

#### INS-5: BitPanel widget

1. Add a signal to the instrument panel
2. Right-click → Change Type → BitPanel
3. BitPanel config dialog appears (or right-click → Configure BitPanel)
4. Set bit count (1-8) and enter labels for each bit
5. Click OK — widget shows labeled bit indicators (green = 1, gray = 0)
6. Values update live as signal data changes

#### INS-6: Instrument layout in project

1. Set up several instrument widgets at different positions and types
2. File > Save Project
3. Close and reopen project
4. Instrument panel restored with same widgets, types, and positions (X/Y coordinates)

#### INS-7: Backward compatibility auto-layout

1. Open an old project that has instrument widgets without X/Y coordinates
2. Widgets auto-arrange in a grid layout instead of stacking at (0,0)

### Instrument Panel Test Checklist

| # | Test | Hardware | Status |
|---|------|----------|--------|
| — | Unit tests (14 instrument-specific) | None | PASS |
| INS-1 | Add signal to instrument panel | None | PASS |
| INS-2 | Widget type selector submenu | None | PASS |
| INS-3 | Remove widget | None | PASS |
| INS-4 | Free-position drag and snap | None | PASS |
| INS-5 | BitPanel widget | None | PASS |
| INS-6 | Layout in project save/load | None | PASS |
| INS-7 | Backward compat auto-layout | None | PASS |

---

## Monitor Protocol Tests (Plan 4 — Firmware Required for On-Target)

### Automated Unit Tests

```bash
cd software
dotnet test CanLinConfig.Tests --filter "MonitorFrameDecoder" -v n
```

10 tests covering:
- Standard CAN2 frame decode (header + data pairing)
- DLC=8 with data[7] nibble in header
- DLC=0 header-only (no data frame)
- LIN bus IDs (LIN1, LIN4)
- Extended frame flag (29-bit)
- Sequence gap detection
- Sequence wrap (255 → 0)
- Mismatched data sequence discard
- Non-monitor frames ignored
- Consecutive headers (pending overwrite)

### Monitor UI Walkthrough (No Connection Needed)

#### MON-1: Monitor control panel visible

1. Launch config tool
2. Go to Bus Monitor tab
3. Verify monitor control bar visible between database bar and trace panel
4. "Monitor:" label, "Enable" checkbox (disabled — not connected), bus checkboxes (CAN1-CAN2, LIN1-4), "Gaps: 0 Drops: 0"

#### MON-2: Enable disabled without connection

1. Verify the Enable checkbox is grayed out when not connected
2. Bus checkboxes should still be checkable (they're state-only until connected)

### Monitor Live Tests (Firmware v0.3.0+ Required)

#### MON-3: Enable monitor

1. Connect to board
2. Enable checkbox becomes active
3. Check "Enable" — sends WRITE_PARAM to firmware
4. If CAN2 has traffic, frames should appear in trace panel with "CAN2" bus tag

#### MON-4: Bus filter

1. Uncheck CAN1, keep CAN2 checked
2. Verify only CAN2 frames appear in monitor stream
3. Re-check CAN1, verify CAN1 frames also appear

#### MON-5: Gap and drop counters

1. Under high traffic load, observe "Gaps:" counter incrementing
2. Click near Drops display (or implement refresh) — shows firmware-side drop count
3. Counters reset when monitor is re-enabled

#### MON-6: Signal decode from monitored frames

1. Assign a DBC file to CAN2
2. Enable monitor with CAN2 traffic
3. Signal panel should show decoded signals from CAN2 frames
4. Graph panel should plot CAN2 signals in real-time

#### MON-7: Export includes monitored frames

1. Enable monitor, capture some CAN2/LIN frames
2. Click Export, save as CSV
3. Open CSV — verify CAN2/LIN frames present with correct bus tag

### Monitor Protocol Test Checklist

| # | Test | Hardware | Status |
|---|------|----------|--------|
| — | Unit tests (10 MonitorFrameDecoder) | None | PASS |
| MON-1 | Control panel visible | None | PASS |
| MON-2 | Enable disabled without connection | None | PASS |
| MON-3 | Enable monitor | Board + CAN2 traffic | PASS |
| MON-4 | Bus filter | Board + CAN2 traffic | N/A (needs 2nd adapter) |
| MON-5 | Gap and drop counters | Board + high traffic | PASS |
| MON-6 | Signal decode from monitored frames | Board + CAN2 + DBC | PASS |
| MON-7 | Export includes monitored frames | Board + CAN2 traffic | N/A (needs 2nd adapter) |

---

## Data Logger Tests (Plan 5 — Firmware Required for On-Target)

### DL-1: LogControlPanel layout

1. Connect to board
2. Go to the **Data Logger** tab
3. Verify mode selector shows options: **Manual**, **Continuous**, **Triggered**
4. Verify buttons:

| Button | Expected when disconnected | Expected when connected, not recording | Expected when recording |
|--------|---------------------------|----------------------------------------|-------------------------|
| Start | Disabled | Enabled | Disabled |
| Stop | Disabled | Disabled | Enabled |
| Erase | Disabled | Enabled | Disabled |
| Refresh | Disabled | Enabled | Enabled |

---

### DL-2: Manual recording

1. Select **Manual** mode
2. Click **Start** — verify status shows "Recording"
3. Send several CAN frames on the bus
4. Click **Stop** — status returns to idle
5. Click **Refresh** — verify entry count > 0

---

### DL-3: Continuous mode

1. Select **Continuous** mode
2. Click **Start** — verify status shows "Recording"
3. While recording, reboot the board (power-cycle or reset)
4. Reconnect in the config tool
5. Verify status still shows "Recording" (auto-resume after reconnect)

---

### DL-4: Triggered mode

1. Select **Triggered** mode
2. Configure trigger: Bus = CAN1, ID = 0x100, Op = Any Match
3. Click **Arm** — verify status shows "Armed"
4. Send a CAN frame with ID 0x100 on CAN1
5. Verify status changes to "Triggered — capturing"

---

### DL-5: Bus mask filter

1. Start a recording session (Manual mode)
2. Uncheck CAN2 in the bus mask controls
3. Send frames on both CAN1 and CAN2
4. Click **Stop**, then **Download**
5. Verify downloaded log contains only CAN1 frames (no CAN2 entries)

---

### DL-6: Download

1. Record some frames, then click **Stop**
2. Click **Download**
3. Verify progress bar advances from 0% to 100%
4. Verify status shows "Complete — N entries" when download finishes

---

### DL-7: Download CRC verification

1. Download a log from the device
2. Verify no CRC error messages are reported during the transfer
3. Download completes without any retry messages in the status area

---

### DL-8: Export CSV

1. After a successful download, click **Export**
2. Choose CSV format, save the file
3. Open the file in a text editor
4. Verify:

| Check | Expected |
|-------|----------|
| First row | Header row with column names (e.g., Timestamp, Bus, ID, DLC, Data) |
| Data rows | One row per frame with correct values |
| Format | Consistent delimiter, timestamps in ascending order |

---

### DL-9: Export ASC/BLF

1. Using the same downloaded data, export as **ASC**
2. Open the resulting file in PCAN-View or a text editor — verify readable ASC format
3. Export the same data as **BLF**
4. Open in PCAN-View or CANalyzer — verify frames are readable and timestamps are correct

---

### DL-10: Feed to Bus Monitor

1. After downloading a log, click **Feed to Bus Monitor**
2. Switch to the **Bus Monitor** tab
3. Verify downloaded frames appear in the trace panel with correct timestamps, bus tags, IDs, and data

---

### DL-11: Log Replay

1. Export a log as CSV
2. Switch to the **Log Replay** panel within the Data Logger tab
3. Click **Load CSV**, select the exported file
4. Verify the frame count is shown after loading
5. Click **Play** — verify frames begin appearing in the Bus Monitor trace panel

---

### DL-12: Replay speed

1. Load a CSV in the Log Replay panel
2. Set speed to **1x**, click **Play**
3. Observe frame timing — verify it matches the original capture timestamps
4. Stop, reset, change speed to **10x**, click **Play**
5. Verify frames play back noticeably faster than at 1x

---

### DL-13: Erase all

1. Ensure there are entries on the device (record a short session if needed)
2. Click **Erase All** and confirm the dialog
3. Click **Refresh**
4. Verify entry count = 0 and wrap count = 0

---

### DL-14: Drop count

1. Generate high bus load (many frames per second on CAN1)
2. Start a Manual recording session
3. Observe the **Drops** counter in LogControlPanel — verify it increments under load
4. Click **Stop**, then **Download**
5. Verify the status area reports the number of gap markers in the downloaded log (one gap marker per firmware-side drop event)

---

### Data Logger Test Checklist

| # | Test | Hardware | Status |
|---|------|----------|--------|
| DL-1 | LogControlPanel — mode selector and button enable states | Board | PASS |
| DL-2 | Manual recording — start, capture, stop, entry count | Board + CAN traffic | PASS |
| DL-3 | Continuous mode — auto-resume after reboot | Board + CAN traffic | PASS |
| DL-4 | Triggered mode — arm, trigger on matching frame | Board + CAN traffic | PASS |
| DL-5 | Bus mask filter — CAN2 excluded from log | Board + CAN1 + CAN2 | N/A (needs 2nd adapter) |
| DL-6 | Download — progress bar and "Complete — N entries" | Board | PASS |
| DL-7 | Download CRC verification — no errors or retries | Board | PASS |
| DL-8 | Export CSV — header row + data rows | Board | PASS |
| DL-9 | Export ASC/BLF — valid in PCAN-View/CANalyzer | Board + PCAN-View | PASS |
| DL-10 | Feed to Bus Monitor — frames appear in trace | Board | PASS |
| DL-11 | Log Replay — load CSV, frames in Bus Monitor | None | PASS |
| DL-12 | Replay speed — 1x timing, 10x faster | None | PASS |
| DL-13 | Erase all — entry count and wrap count reset to 0 | Board | PASS |
| DL-14 | Drop count — increments under load, gap markers in log | Board + high traffic | PASS |

---

## Known Limitations

| Item | Detail |
|------|--------|
| **Kvaser adapter** | Full implementation skeleton but untested — requires Kvaser CANlib SDK |
| **Profile JSON** | LIN IDs and data are placeholder values — fill in from real device datasheets |
| **BULK_START CRC** | 24-bit (lower 3 bytes of CRC32). Incompatible with pre-Phase 8 firmware |
| **Recent Projects menu** | Recent projects list is tracked in settings.json but not yet exposed as a submenu in the File menu |
