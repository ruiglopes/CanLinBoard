# Bus Monitor & Data Logger — Design Spec

**Date:** 2026-03-23
**Status:** Approved

## Overview

Add live bus monitoring with signal decoding/graphing and on-board data logging to the CAN/LIN gateway platform. Covers firmware streaming protocol, flash-based ring buffer logger, config tool visualization (trace, signals, graphs, instrument panels), and log export in industry-standard formats.

## Requirements

1. **Live bus monitor** — real-time view of all 6 buses (CAN1, CAN2, LIN1-4) from the config tool
2. **Transport** — mirror on CAN1 (primary), USB CDC sideband (future upgrade path)
3. **Signal decoding** — DBC for CAN, LDF for LIN, with live value table and time-series graphing
4. **On-board logger** — secondary flash ring buffer with manual, continuous, and triggered recording modes
5. **Log export** — CSV, ASC (Vector text), BLF (Vector binary) formats
6. **UI** — "Bus Monitor" and "Data Logger" tabs with self-contained panels, designed for future AvalonDock docking
7. **Instrument panel** — INCA-style drag-and-drop signal monitoring with gauges, bars, and numeric displays (follow-on phase)
8. **Database management** — per-bus DBC/LDF assignment
9. **Project system** — portable `.clpkg` bundles (ZIP) capturing entire tool state: databases, instrument layouts, adapter settings, logger config, profiles. Configurable auto-load of last project on startup.

## Implementation Approach

Config tool-first: build the bus monitor UI, signal decoding, and graphing using CAN1 traffic the tool already sees. Then add the firmware monitor protocol to bring in CAN2/LIN. Logger comes last. This allows rapid UI iteration without firmware changes.

---

## 1. Architecture

### Three Layers

```
Config Tool (WPF)
├── Bus Monitor Tab
│   ├── TracePanel (UserControl)
│   ├── SignalPanel (UserControl)
│   ├── GraphPanel (UserControl)
│   └── InstrumentPanel (UserControl, future phase)
├── Data Logger Tab
│   ├── LogControlPanel (UserControl)
│   ├── LogDownloadPanel (UserControl)
│   └── LogReplayPanel (UserControl)
├── Services
│   ├── BusDataService (central pipeline: tag, decode, broadcast)
│   ├── DatabaseManager (per-bus DBC/LDF, signal lookup)
│   ├── LogWriteService (record frames to file)
│   └── LogReplayService (playback from file)
├── Adapters
│   ├── ICanAdapter (existing)
│   └── IStreamTransport (future USB CDC)
└── Exporters
    ├── CsvExporter
    ├── AscExporter
    └── BlfExporter

Firmware (RP2350)
├── Bus Monitor Protocol (mirror → CAN1)
├── Flash Logger (ring buffer on CS1)
└── CAN1 / CAN2 / LIN1-4 (existing drivers)
```

### Key Design Decisions

- **BusDataService** is the central hub — receives raw frames from any transport, tags with source bus, routes to signal decoder, feeds all panels. Performs WPF dispatcher marshaling so all consumers can assume UI thread.
- **BusFrame** extends `CanFrame` with `SourceBus` property — intermediate representation used throughout the pipeline
- **Panels are self-contained UserControls** with their own ViewModels — reusable and ready for future AvalonDock integration
- **IStreamTransport** is a future abstraction for USB CDC — same frame format as the CAN1 mirror protocol, different pipe
- **Replay reuses the live pipeline** — `LogReplayService` feeds `BusDataService` identically to `ICanAdapter`, so all panels work with both live and replayed data

---

## 2. Firmware Monitor Protocol

### CAN IDs

| CAN ID | Purpose |
|--------|---------|
| 0x604  | Monitor header: sequence + source bus + original ID + DLC + timestamp |
| 0x605  | Monitor data: sequence + original frame payload (up to 7 bytes) |

Note: 0x604/0x605 extend the existing config protocol block (0x600-0x603), avoiding collision with OBD-II diagnostic IDs (0x7E0-0x7E7) which would conflict on vehicle CAN buses.

### Header Frame (0x604, 8 bytes)

```
Byte 0:    sequence number (0-255, wrapping, pairs header with data)
Byte 1:    source bus [3:0] + flags [7:4] (bit4=extended ID flag, bits 5-7 reserved)
Bytes 2-5: original frame ID (32-bit LE — supports both 11-bit standard and 29-bit extended CAN IDs, also LIN PIDs)
Byte 6:    original DLC [3:0] + 8th data byte [7:4] (for DLC=8, upper nibble carries data[7])
Byte 7:    timestamp delta low byte (ms, mod 256, relative to monitor start — for inter-frame jitter analysis)
```

### Data Frame (0x605, up to 8 bytes)

```
Byte 0:    sequence number (must match preceding header)
Bytes 1-7: original frame data (up to 7 payload bytes)
```

For frames with DLC <= 7, the data frame carries all payload bytes at positions 1..DLC. For DLC=8, the data frame carries data[0..6] at positions 1-7, and data[7] is encoded in the upper nibble of header byte 6. This avoids needing a third CAN frame.

### Protocol Rules

- Header always sent first, data frame follows. The sequence number in both frames pairs them unambiguously — the config tool matches 0x605 to the most recent 0x604 with the same sequence byte.
- DLC=0 frames: header only, no data frame
- Sequence number increments per monitored frame (wraps 0→255). If the config tool sees a sequence gap, it knows frames were dropped.
- Monitor enabled/disabled via config protocol (`SectionMonitor = 0x06`)
- Configurable filter: bus selection bitmask, optional ID whitelist/blacklist
- Config protocol (0x600-0x603), diagnostics (0x7F0-0x7F4), and bootloader (0x700+) frames are never mirrored

### Monitor Execution Model

Monitor logic runs **inline in can_task and lin_task**, not as a separate FreeRTOS task. These tasks already see every frame. When monitoring is enabled, each task builds and queues monitor frames immediately after processing the original frame. This avoids cross-task queuing overhead and an additional task's stack allocation.

### Backpressure and Priority

Monitor frames are queued via a **dedicated monitor TX queue** (`g_monitor_tx_queue`, depth 16). The CAN TX path in `can_task` drains the monitor queue only when the main `g_can_tx_queue` is empty, ensuring application traffic (gateway routed frames, config responses) always takes priority. If the monitor queue is full, new monitor frames are silently dropped (oldest-in-queue dropped). The sequence number gap tells the config tool that frames were lost.

### Bus Load Estimate

Each monitored frame = 2 CAN frames on CAN1. At 500 kbps, CAN1 theoretical max ~4000 frames/sec. With 50% headroom for application traffic: ~1000 monitored frames/sec capacity.

### Timestamp Synchronization

All frames displayed in the config tool use **host-side timestamps** (DateTime from the adapter RX callback). Firmware timestamps in monitor frames are used only for relative ordering and jitter analysis between monitored frames. This avoids the complexity of synchronizing firmware and host clocks.

### Future USB CDC Path

Same header+data byte format streamed over USB serial. No 8-byte CAN limit means header and data can merge into a single packet. `IStreamTransport` abstraction in config tool makes the transport transparent to consumers.

---

## 3. On-Board Flash Logger

### Secondary Flash (CS1) Address Map

```
0x000000 - 0x01FFFF  NVM config reserved (128 KB, 32 sectors)
                      Currently uses 12 KB (3 sectors: Slot A, Slot B, Meta).
                      Remaining 116 KB reserved for future config growth.
0x020000 - 0x020FFF  Logger metadata (4 KB, 1 sector)
0x021000 - 0xFFFFFF  Log data ring buffer (~16,252 KB)
```

Note: The NVM driver (`hal_flash_nvm.c`) must be updated with a bounds check to prevent writes beyond offset 0x01FFFF, guarding against accidental overwrites into logger space.

### Logger Metadata (0x020000)

```c
typedef struct {
    uint32_t magic;           // 0x4C4F4701 ("LOG\x01")
    uint32_t write_offset;    // current write position in ring buffer
    uint32_t wrap_count;      // times the ring has wrapped
    uint32_t start_timestamp; // absolute ms when logging started
    uint8_t  state;           // 0=idle, 1=recording, 2=triggered-armed, 3=triggered-capturing
    uint8_t  mode;            // 0=manual, 1=continuous, 2=triggered
    uint16_t reserved;
    uint32_t trigger_id;      // CAN/LIN ID that fires the trigger
    uint8_t  trigger_bus;     // which bus to watch
    uint8_t  trigger_byte;    // data byte index to compare
    uint8_t  trigger_op;      // 0=any, 1=equals, 2=gt, 3=lt, 4=mask
    uint8_t  trigger_value;   // comparison value
    uint32_t pre_trigger_kb;  // pre-trigger data to keep
    uint32_t post_trigger_kb; // post-trigger data to capture after trigger
    uint32_t crc32;           // metadata integrity check
} log_metadata_t;
```

### Log Entry Format (20 bytes, packed)

```c
typedef struct __attribute__((packed)) {
    uint32_t timestamp_ms;   // relative to start_timestamp
    uint32_t frame_id;       // CAN ID (supports future 29-bit extended IDs) or LIN PID
    uint8_t  bus;            // source bus enum
    uint8_t  dlc;            // 0-8
    uint8_t  data[8];        // frame payload
    uint16_t reserved;       // pad to 20 bytes (aligned, efficient flash writes)
} log_entry_t;
```

**Capacity:** ~812,600 entries in the ring buffer. At 1000 frames/sec: ~13.5 minutes. At 100 frames/sec: ~2.25 hours. At typical automotive loads (200-400 frames/sec): ~34-68 minutes.

### Recording Modes

1. **Manual** — config tool sends start/stop. Firmware writes between start and stop.
2. **Continuous** — always recording, ring wraps oldest data. During download, firmware buffers incoming frames to a small RAM queue (64 entries). If the RAM queue fills, frames are dropped and a gap marker entry is written when logging resumes: a `log_entry_t` with `bus = 0xFF` (reserved sentinel), `frame_id = number of dropped frames`, and `timestamp_ms` of the resume point. The config tool recognizes bus=0xFF as a gap indicator.
3. **Triggered** — armed via config tool. Firmware watches for trigger condition. On trigger, retains `pre_trigger_kb` of existing data, captures `post_trigger_kb` more, then stops.

### Flash Write Error Handling

- On page program failure: retry once. If second attempt fails, skip to next page boundary and continue logging. Increment an error counter in the metadata.
- On sector erase failure: mark sector as skipped (advance write_offset past it), log warning. The ring buffer effectively shrinks by one sector.
- Error count readable via `SectionLog` param (config tool displays flash health).
- If error count exceeds threshold (e.g., 10), firmware stops logging and sets status to error state.

### Download Protocol — Chunked Read

The existing BULK_READ protocol (16-bit size, 2 KB staging buffer) does not scale to multi-MB log downloads. A new **chunked log read** mechanism is used:

**Sequence:**
1. Config tool reads log metadata via `READ_PARAM` on `SectionLog` (entry count, write offset, wrap count)
2. Config tool sends `LOG_READ_CHUNK` command: `[cmd=0x24][offset_3B_LE][length_2B_LE]` on 0x600
   - offset: 24-bit byte offset into the log ring buffer (16 MB addressable)
   - length: 16-bit chunk size (max 4096 bytes = one sector)
3. Firmware responds with data stream on 0x603 (`ConfigBulkRespId`): sequential 8-byte frames, reusing the existing BULK_READ_DATA flow. This avoids collision with 0x605 (monitor data) and aligns with existing config tool adapter filtering.
4. Final frame followed by ACK on 0x601 with CRC32 of the chunk
5. Config tool requests next chunk. Repeat until all data read.

This allows the config tool to download in manageable 4 KB chunks with per-chunk CRC verification, progress reporting, and resume capability (just restart from the last successful chunk offset).

### Flash Wear

W25Q128 rated 100K erase cycles per sector. Ring buffer spans ~4063 sectors. At worst case continuous recording (1000 frames/sec), the ring wraps every ~813 seconds (~13.5 minutes). Each individual sector is erased once per wrap = ~85 erases/day. At 100K cycle rating: ~3.2 years of continuous 24/7 recording at maximum rate. At typical loads: decades of lifetime.

---

## 4. Config Tool — Bus Monitor Tab

### TracePanel

- Columns: timestamp, bus (color-coded), ID (hex), DLC, data (hex), decoded message name
- Virtualized ListView backed by fixed-size circular buffer (not ObservableCollection — performance)
- Pause/resume, clear, ID filter, bus filter, text search
- Right-click signal → "Add to Graph"

### SignalPanel

- Live signal value table: name, current value, unit, min/max seen, source message, last update
- Updated on every matching frame decode
- Sortable, filterable by bus/message
- Populated from loaded DBC/LDF databases

### GraphPanel

- Time-series plotting with **ScottPlot 5.x** (WPF, MIT license, handles 100K+ points)
- User selects signals from SignalPanel or TracePanel to plot
- Scrolling X-axis (time), auto-scaling Y-axis
- Multiple signals on same chart with legend
- Pause to inspect, zoom, cursor readout

### InstrumentPanel (Future Phase)

- Grid-based layout, drag signals from SignalPanel
- Widget types:
  - **Numeric display** — large value + unit + min/max
  - **Gauge** — radial with configurable range and color zones
  - **Bar meter** — vertical/horizontal fill
  - **Boolean indicator** — on/off lamp for single-bit signals
  - **Text/enum** — state display for enumerated signals
- Layouts saveable/loadable (future workspace file)

---

## 5. Config Tool — Data Logger Tab

### LogControlPanel

- Mode selector: Manual / Continuous / Triggered
- Trigger configuration (when triggered mode): bus, ID, byte index, operator, value, pre/post size
- Bus filter checkboxes
- Status: recording state, entry count, wrap count, flash usage %, flash error count
- Start / Stop / Arm buttons
- Settings sent via config protocol (`SectionLog = 0x07`)

### LogDownloadPanel

- Download button → reads metadata, chunked log read with progress bar
- Summary: entry count, time span, buses captured
- Downloads to memory, user chooses export format
- Resume support: tracks last successful chunk offset

### LogReplayPanel

- Loads exported log (CSV/BLF/ASC) or fresh download
- Playback controls: play, pause, speed (1x/2x/5x/10x), timeline scrub
- Feeds `BusDataService` — all panels (Trace, Signal, Graph, Instrument) work identically
- Timeline bar with trigger point marker

---

## 6. Data Pipeline

### BusDataService (Central Hub)

```
Frame Sources              BusDataService              Consumers
─────────────              ──────────────              ─────────
ICanAdapter ──┐            - Tag source bus            ┌── TracePanel
LogReplay ────┤──────────► - Decode via DatabaseMgr ──►├── SignalPanel
(USB future) ─┘            - Buffer history            ├── GraphPanel
                           - WPF dispatcher marshal    ├── InstrumentPanel
                                                       ├── DiagnosticsVM
                                                       └── LogWriteService
```

- BusDataService owns the WPF dispatcher reference and marshals all consumer callbacks to the UI thread
- Configurable history buffer for graph scrollback
- Existing `DiagnosticsViewModel` continues to decode its own diagnostic frames (heartbeat, stats, crash) but receives them through BusDataService instead of directly from ConfigProtocol. The frame log moves to TracePanel.

### DatabaseManager

- Per-bus DBC/LDF file assignment (CAN1, CAN2, LIN1-4)
- Assignments stored in user settings (file paths + bus mapping)
- Parse on load, cache signal lookup tables
- API: `GetSignals(busId, frameId) → List<SignalValue>`
- New `SignalExtractor` utility: takes frame data bytes + signal definition → physical value (handles bit position, length, byte order, factor, offset)
- Existing `DbcParser` already parses signal definitions (start bit, length, byte order, factor, offset, min/max, unit) — no parser changes needed
- New `LdfParser` for LIN databases — scoped to unconditional frames with scalar signal encoding (diagnostic frames, configurable frames, and BCD/ASCII encoding types are out of scope for initial implementation)

### SignalValue Model

```csharp
record SignalValue(
    string Name,          // e.g. "EngineRPM"
    double RawValue,      // integer from bitfield
    double PhysicalValue, // raw * factor + offset
    string Unit,          // "rpm"
    byte Bus,             // source bus
    uint FrameId,         // parent message ID
    DateTime Timestamp);
```

---

## 7. Export Formats

### CsvExporter

- One row per frame: `timestamp_ms, bus, id_hex, dlc, data_hex, [signal_columns]`
- Optional decoded signal expansion when DBC/LDF loaded
- Header row with column names

### AscExporter (Vector ASC)

- Text format: `timestamp channel id dir dlc data`
- Example: `0.001234 1 0x123 Rx d 8 01 02 03 04 05 06 07 08`
- Channel mapping: CAN1→1, CAN2→2, LIN→3-6
- Header with date, base hex, absolute timestamps

### BlfExporter (Vector BLF)

- Binary container, zlib-compressed object records
- Object types: `CAN_MESSAGE` (0x01), `LIN_MESSAGE` (0x06)
- Native support in CANalyzer/CANoe/PCAN-View
- Consider `Vector.BLF` NuGet package if available, otherwise implement from public spec

### Export Priority

1. CSV (trivial)
2. ASC (text-based, quick)
3. BLF (binary, most effort — evaluate NuGet package vs. manual implementation)

---

## 8. Config Protocol Extensions

### New Section IDs

| Section | ID | Purpose |
|---------|----|---------|
| SectionMonitor | 0x06 | Bus monitor enable/disable, filter config |
| SectionLog | 0x07 | Logger mode, trigger config, start/stop/arm, download |

Both firmware (`config_protocol.h`) and config tool (`ProtocolConstants.cs`) must be updated with these new section IDs and corresponding handler cases.

### New Command Code

| Command | Code | Purpose |
|---------|------|---------|
| CmdLogReadChunk | 0x24 | Chunked log download (offset + length) |

### Monitor Commands (SectionMonitor = 0x06)

| Param | R/W | Width | Description |
|-------|-----|-------|-------------|
| 0 | R/W | 1 byte | Monitor enable (0=off, 1=on) |
| 1 | R/W | 1 byte | Bus filter bitmask (bit0=CAN1, bit1=CAN2, bit2-5=LIN1-4) |
| 2 | R/W | 1 byte | ID filter mode (0=none, 1=whitelist, 2=blacklist) |
| 3 | R | 1 byte | Monitor TX drop count (wrapping, for diagnostics) |

Whitelist/blacklist ID lists transferred via BULK_WRITE to SectionMonitor. Maximum 32 IDs per list (128 bytes). Stored in RAM only (lost on reboot — monitor is a live debugging feature, not a persistent config).

### Logger Commands (SectionLog = 0x07)

| Param | R/W | Width | Description |
|-------|-----|-------|-------------|
| 0 | R/W | 1 byte | Logger mode (0=manual, 1=continuous, 2=triggered) |
| 1 | R/W | 1 byte | Bus filter bitmask |
| 2 | R/W | 1 byte | Logger state command (0=stop, 1=start, 2=arm) |
| 3 | R | 1 byte | Logger status (0=idle, 1=recording, 2=armed, 3=capturing, 4=error) |
| 4 | R | 4 bytes | Entry count |
| 5 | R | 4 bytes | Wrap count |
| 6 | R/W | 1 byte | Trigger bus |
| 7 | R/W | 4 bytes | Trigger ID (32-bit for extended CAN ID support) |
| 8 | R/W | 1 byte | Trigger byte index |
| 9 | R/W | 1 byte | Trigger operator |
| 10 | R/W | 1 byte | Trigger value |
| 11 | R/W | 2 bytes | Pre-trigger KB |
| 12 | R/W | 2 bytes | Post-trigger KB |
| 13 | R | 2 bytes | Flash error count |

Log data download via chunked read command (0x24).

---

## 9. Project System

### Overview

A project file (`.clpkg`) bundles the entire config tool state into a portable zipped package. Opening a project restores the tool exactly as it was — databases loaded, panels configured, adapter selected. Sharing a `.clpkg` with a colleague gives them a working setup with no manual configuration.

### File Format

A `.clpkg` file is a standard ZIP archive containing:

```
project.clpkg (ZIP)
├── project.json          ← master manifest (all settings, references)
├── databases/            ← embedded copies of DBC/LDF files
│   ├── can1.dbc
│   ├── can2.dbc
│   ├── lin1.ldf
│   └── ...
├── instruments/          ← instrument panel layouts
│   ├── main-dashboard.json
│   └── ...
├── profiles/             ← device profiles referenced by this project
│   └── ...
└── exports/              ← (optional) saved log export presets
```

### project.json Manifest

```json
{
  "version": 1,
  "name": "WDA Wiper Test Bench",
  "created": "2026-03-23T10:00:00Z",
  "modified": "2026-03-23T14:30:00Z",

  "connection": {
    "adapterType": "PCAN",
    "channel": "PCAN_USBBUS1",
    "bitrate": 500000
  },

  "databases": {
    "can1": "databases/can1.dbc",
    "can2": "databases/can2.dbc",
    "lin1": "databases/lin1.ldf",
    "lin2": null,
    "lin3": null,
    "lin4": null
  },

  "busMonitor": {
    "busFilter": ["CAN1", "CAN2", "LIN1"],
    "idFilter": "",
    "graphSignals": ["EngineRPM", "CoolantTemp"],
    "graphTimeWindow": 30
  },

  "dataLogger": {
    "mode": "triggered",
    "busFilter": ["CAN1", "CAN2"],
    "trigger": {
      "bus": "CAN1",
      "id": 291,
      "byteIndex": 0,
      "operator": "gt",
      "value": 128,
      "preKb": 512,
      "postKb": 1024
    }
  },

  "instruments": [
    "instruments/main-dashboard.json"
  ],

  "deviceConfig": {
    "activeProfile": "WdaWiper",
    "diagCanId": 2032,
    "diagIntervalMs": 1000
  }
}
```

### Scope — What the Project Captures

| Category | Settings captured |
|----------|-------------------|
| **Connection** | Adapter type, channel, bitrate |
| **Databases** | DBC/LDF files (embedded copies), per-bus assignments |
| **Bus Monitor** | Bus filter, ID filter, graph signal selections, time window |
| **Data Logger** | Mode, bus filter, trigger configuration |
| **Instrument Panels** | Layouts, widget configs, signal assignments |
| **Device Config** | Active profile, diagnostics settings |
| **Profiles** | Device profiles referenced by this project |

### Startup Behavior

Configurable in application settings (`%AppData%/CanLinConfig/settings.json`):

```json
{
  "startup": {
    "loadLastProject": false,
    "lastProjectPath": "C:/Users/.../my-bench.clpkg"
  }
}
```

- `loadLastProject = false` (default): tool starts blank, user opens a project manually via File → Open Project
- `loadLastProject = true`: tool automatically loads the last opened project on startup
- User toggles this via Settings dialog or a "Load last project on startup" checkbox

### Project Operations

- **File → New Project** — creates a new project from current tool state
- **File → Open Project** — loads a `.clpkg`, extracts databases to a temp directory, applies all settings
- **File → Save Project** — overwrites the current `.clpkg` with current state
- **File → Save Project As** — saves to a new `.clpkg` file
- **File → Close Project** — returns to blank state
- **Recent Projects** submenu — quick access to recently opened projects

### Implementation: ProjectService

```csharp
public class ProjectService
{
    // Core operations
    Task<Project> OpenAsync(string path);
    Task SaveAsync(Project project, string path);
    Project CreateFromCurrentState();
    void ApplyToCurrentState(Project project);

    // State tracking
    Project? CurrentProject { get; }
    bool HasUnsavedChanges { get; }
    event EventHandler? ProjectChanged;
}
```

- `Project` model holds the deserialized `project.json` plus paths to extracted temp files
- On open: extract ZIP to `%TEMP%/CanLinConfig/projects/<guid>/`, load databases from extracted paths
- On save: serialize current state to `project.json`, re-zip with database files from their current locations
- Unsaved changes tracking: set dirty flag on any setting change, prompt on close/exit

---

## 10. New CAN IDs Summary

| CAN ID | Purpose | Added by |
|--------|---------|----------|
| 0x604 | Monitor header (seq + bus + ID + DLC + timestamp) | This spec |
| 0x605 | Monitor data (seq + payload) | This spec |

Update `docs/CanLinBoard.dbc` when implemented.

---

## 10. SRAM Budget

Current task stacks: CAN(512w) + LIN(512w) + Gateway(1024w) + Config(768w) + Diag(512w) = 13,312 bytes + TCBs.

This spec adds:
- Monitor TX queue: FreeRTOS queue (16 items × 16 bytes + ~96 bytes queue control block) ≈ 352 bytes
- Monitor filter list: 32 × 4 bytes = 128 bytes (RAM only)
- Logger RAM buffer (continuous mode download): 64 × 20 bytes = 1,280 bytes
- Logger write page buffer: 256 bytes
- No new FreeRTOS tasks

**Total additional SRAM: ~2,016 bytes.** Well within the RP2350's 520 KB SRAM.

---

## 11. Implementation Phases

### Phase A: Config Tool — Bus Monitor Foundation
- BusDataService pipeline with dispatcher marshaling
- BusFrame model (CanFrame + SourceBus)
- DatabaseManager with per-bus DBC assignment
- SignalExtractor utility (bit-level DBC signal decoding)
- TracePanel (virtualized, bus-tagged)
- SignalPanel (live value table)
- GraphPanel (ScottPlot 5.x integration)
- Per-bus database assignment UI
- DiagnosticsViewModel refactor (receives frames via BusDataService)
- Works immediately with CAN1 traffic via existing adapter

### Phase A2: Project System
- ProjectService (open/save/create/apply)
- Project model and project.json schema
- `.clpkg` ZIP packaging (embed DBC/LDF files, instrument layouts)
- File menu: New / Open / Save / Save As / Close / Recent Projects
- Application settings: `loadLastProject` toggle + last project path
- Unsaved changes tracking and save prompt on close/exit
- All subsequent features store their state through the project system

### Phase B: LDF Parser & Signal Search
- LdfParser for LIN databases (unconditional frames, scalar signals)
- Signal search and filtering across all loaded databases
- Per-bus LDF assignment in DatabaseManager

### Phase C: Firmware Monitor Protocol
- Monitor logic inline in can_task and lin_task
- Dedicated monitor TX queue with lower priority than application traffic
- Sequence-numbered header (0x604, 32-bit frame ID) + data (0x605) frame pairs
- Config protocol SectionMonitor (0x06) — enable, bus filter, ID filter
- Config tool: unwrap monitor frames by sequence number, tag source bus, feed BusDataService
- On-target testing

### Phase D: On-Board Logger — Firmware
- Flash logger ring buffer driver (CS1, offset 0x020000+)
- NVM driver bounds check (prevent writes beyond 0x01FFFF)
- Logger metadata sector
- Three recording modes (manual, continuous, triggered)
- Flash write error handling (retry, skip, error counter)
- Config protocol SectionLog (0x07) — mode, trigger, start/stop/arm
- Chunked log read command (0x24) for multi-MB downloads (data on 0x603)

### Phase E: Config Tool — Data Logger Tab
- LogControlPanel (mode, trigger config, status, flash health)
- LogDownloadPanel (chunked read + progress + resume)
- LogReplayPanel (playback into BusDataService)

### Phase F: Export Formats
- CsvExporter
- AscExporter
- BlfExporter (evaluate Vector.BLF NuGet vs. manual implementation)
- Export UI with format selection

### Phase G: Instrument Panel (Future)
- InstrumentPanel UserControl
- Drag-and-drop signal assignment
- Widget library (gauge, bar, numeric, boolean, enum)
- Layout save/load

### Phase H: USB CDC Sideband (Future)
- Firmware USB CDC stack
- IStreamTransport abstraction in config tool
- Same monitor frame format over serial
- Automatic transport selection (USB preferred, CAN1 fallback)

---

## 12. ROADMAP.md Updates

This spec supersedes:
- P4 #23 (Data logging to secondary flash)
- P3 #20 partially (Frame monitor auto-scroll — covered by TracePanel)

Update ROADMAP.md to reference this spec when implementation begins.
