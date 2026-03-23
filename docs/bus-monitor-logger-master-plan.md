# Bus Monitor & Data Logger — Master Plan

**Spec:** [`docs/superpowers/specs/2026-03-23-bus-monitor-logger-design.md`](superpowers/specs/2026-03-23-bus-monitor-logger-design.md)
**Date:** 2026-03-23
**Branch:** `feature/bus-monitor-foundation`

---

## Overview

This document tracks all implementation plans for the bus monitor and data logger feature set. Each plan is a self-contained unit that produces working, testable software. Plans are executed in order — each builds on the previous.

---

## Plan 1: Bus Monitor Foundation (Config Tool)
**Status:** COMPLETE
**Plan:** [`docs/superpowers/plans/2026-03-23-bus-monitor-foundation.md`](superpowers/plans/2026-03-23-bus-monitor-foundation.md)
**Commits:** 8 (on `feature/bus-monitor-foundation`)
**Tests:** 28 passing

### What was built
- **BusDataService** — central pipeline: receives frames, tags with source bus, decodes signals, broadcasts to consumers
- **SignalExtractor** — bit-level DBC signal extraction (Intel/Motorola byte order, signed/unsigned, factor/offset scaling)
- **DatabaseManager** — per-bus DBC file assignment with signal lookup cache
- **TracePanelViewModel** — high-performance frame trace list with ID/bus filtering and pause
- **SignalPanelViewModel** — live signal value table with min/max tracking
- **GraphPanelViewModel** — ScottPlot 5.x time-series plotting with signal trace management
- **Bus Monitor tab** — new tab in MainWindow with 3 resizable panels (trace, signals, graph) and DBC file assignment bar
- **Integration** — MainViewModel wires adapter frames through BusDataService while preserving existing diagnostics decode

### What it enables
Connect to a CAN adapter, open the Bus Monitor tab, load a DBC file, and see live CAN1 traffic with decoded signals and real-time graphing. Works immediately with no firmware changes.

---

## Plan 2: Project System
**Status:** COMPLETE
**Plan:** [`docs/superpowers/plans/2026-03-23-project-system.md`](superpowers/plans/2026-03-23-project-system.md)
**Commits:** 4 (on `feature/bus-monitor-foundation`)
**Tests:** 11 new (39 total)

### What was built
- **AppSettings** — user preferences persisted to `%AppData%/CanLinConfig/settings.json` (auto-load last project, recent projects list)
- **Project model** — `Project`, `ProjectManifest`, `ProjectState` DTOs with JSON schema
- **ProjectService** — create/open/save/apply `.clpkg` ZIP bundles with embedded DBC files
- **File menu** — File > New / Open / Save / Save As / Close with Ctrl+N/O/S shortcuts
- **Window title** — shows project name and dirty indicator (*)
- **Unsaved changes prompt** — Yes/No/Cancel on close or project switch

### What it enables
Save the entire config tool state (connection settings, DBC assignments, graph config) as a portable `.clpkg` file. Share with colleagues — they get the same working environment. Auto-load last project on startup (configurable).

### Key decisions
- `.clpkg` is a standard ZIP archive with `project.json` manifest
- Databases embedded as copies (portable — share with colleagues)
- Startup behavior configurable: manual or auto-load last project

### Dependencies
- Plan 1 (BusMonitor settings need persisting)

---

## Plan 3: LDF Parser Integration
**Status:** COMPLETE
**Plan:** [`docs/superpowers/plans/2026-03-23-ldf-integration.md`](superpowers/plans/2026-03-23-ldf-integration.md)
**Commits:** 2 (on `feature/bus-monitor-foundation`)
**Tests:** 6 new (45 total)

### What was built
- **DatabaseManager LDF support** — auto-detects .ldf vs .dbc by extension, converts LDF frame signals to DbcSignal entries with encoding-derived factor/offset/unit
- **LIN1-4 database assignment UI** — second row in Bus Monitor database bar with assign/clear buttons for all 4 LIN buses
- **Unified file dialog** — accepts both DBC and LDF files
- **Project system wiring** — LIN database paths captured/restored in project save/load

### What it enables
Assign LDF files to LIN buses in the Bus Monitor tab. When the firmware monitor protocol (Plan 4) delivers LIN frames, they'll be decoded with signal names and physical values automatically.

### Key decisions
- LDF parser was already implemented (381 lines) — this plan only added the DatabaseManager integration layer
- LIN signals always little-endian, unsigned — matches LIN protocol spec
- Signals with only logical encodings (no physical_value) get Factor=1, Offset=0 defaults

### Dependencies
- Plan 1 (DatabaseManager, SignalExtractor)

---

## Plan 4: Firmware Monitor Protocol
**Status:** COMPLETE
**Plan:** [`docs/superpowers/plans/2026-03-23-firmware-monitor-protocol.md`](superpowers/plans/2026-03-23-firmware-monitor-protocol.md)
**Commits:** 9 (on `feature/bus-monitor-foundation`)
**Tests:** 10 new (117 total)

### Scope

**Firmware side:**
- Monitor logic inline in `can_task` and `lin_task` (no new FreeRTOS task)
- Dedicated `g_monitor_tx_queue` (depth 16), drained only when main TX queue is empty
- Sequence-numbered header (0x604, 32-bit frame ID) + data (0x605) frame pairs
- Config protocol `SectionMonitor` (0x06) — enable, bus filter, ID whitelist/blacklist

**Config tool side:**
- Unwrap monitor frames by sequence number
- Tag source bus, feed BusDataService
- Monitor control panel in Bus Monitor tab (enable/disable, bus checkboxes, filter config)
- Sequence gap detection (dropped frame indicator)

### Key decisions
- CAN IDs 0x604/0x605 (extends config protocol block, avoids OBD-II collision)
- Monitor state is RAM-only (not persisted to NVM — debugging feature)
- Max 32 IDs per whitelist/blacklist

### Dependencies
- Plan 1 (BusDataService, Bus Monitor tab)

---

## Plan 5: On-Board Logger + Data Logger Tab
**Status:** NOT STARTED
**Spec sections:** 3 (Flash Logger), 5 (Data Logger Tab), 8 (Config Protocol — SectionLog)

### Scope

**Firmware side:**
- Flash logger ring buffer driver (CS1, offset 0x020000+)
- NVM driver bounds check (prevent writes beyond 0x01FFFF)
- Logger metadata sector at 0x020000
- Three recording modes: manual, continuous, triggered
- Flash write error handling (retry, skip, error counter, gap markers)
- Config protocol `SectionLog` (0x07) — mode, trigger config, start/stop/arm
- Chunked log read command (0x24) for multi-MB downloads (data on 0x603)

**Config tool side:**
- LogControlPanel — mode selector, trigger config, status, flash health
- LogDownloadPanel — chunked read with progress bar and resume
- LogReplayPanel — playback into BusDataService (all panels work with replayed data)
- Data Logger tab in MainWindow

### Key decisions
- 128 KB reserved for NVM config, log starts at 0x020000
- `log_entry_t` is 20 bytes with `uint32_t frame_id` (29-bit ready)
- Continuous mode buffers 64 entries in RAM during download; gap marker = bus=0xFF sentinel
- Chunked download: 4 KB chunks with per-chunk CRC32

### Dependencies
- Plan 1 (BusDataService, Bus Monitor panels for replay)
- Plan 4 (monitor protocol — shared firmware infrastructure)
- Requires on-target testing

---

## Plan 6: Export Formats
**Status:** COMPLETE
**Plan:** [`docs/superpowers/plans/2026-03-23-export-formats.md`](superpowers/plans/2026-03-23-export-formats.md)
**Commits:** 3 (on `feature/bus-monitor-foundation`)
**Tests:** 16 new (61 total)

### What was built
- **IFrameExporter interface** — common contract for all exporters
- **CsvExporter** — header + data rows with optional DBC/LDF-decoded signal columns
- **AscExporter** — Vector ASC text format with bus-to-channel mapping (CAN1=1, CAN2=2, LIN=3-6)
- **BlfExporter** — Vector BLF binary format (v4.0.0, uncompressed containers, CAN_MESSAGE objects)
- **Export button** in Bus Monitor database bar with Save dialog (CSV/ASC/BLF filter)
- **Integration tests** — mixed bus frames across all exporters, CSV with LDF signal decode

### What it enables
Click Export in the Bus Monitor tab, choose CSV/ASC/BLF format, save captured frames. CSV opens in Excel/MATLAB with signal columns. ASC/BLF open in PCAN-View, CANalyzer, CANoe.

### Key decisions
- No .NET BLF NuGet exists — implemented from public format spec (python-can reference)
- BLF uses uncompressed containers (simplest, compatible with all Vector tools)
- All exporters implement `IFrameExporter` for uniform handling

### Dependencies
- Plan 1 (BusFrame model, BusDataService.FrameHistory)

---

## Plan 7: Instrument Panel
**Status:** COMPLETE
**Plan:** [`docs/superpowers/plans/2026-03-23-instrument-panel.md`](superpowers/plans/2026-03-23-instrument-panel.md)
**Commits:** 2 (on `feature/bus-monitor-foundation`)
**Tests:** 7 new (68 total)

### What was built
- **InstrumentWidget model** — observable widget with signal binding, value/min/max tracking, normalized 0-1 output for gauges/bars
- **InstrumentPanelViewModel** — widget collection management, signal routing, layout serialization
- **5 widget UserControls** — NumericWidget (large value+unit+min/max), BarWidget (fill bar with ScaleTransform), GaugeWidget (radial arc with needle), BooleanWidget (on/off lamp), EnumWidget (text state)
- **InstrumentPanel view** — WrapPanel layout with DataTemplateSelector, right-click context menu (Change Type / Remove)
- **Signal panel integration** — right-click "Add to Instrument Panel" context menu
- **Bus Monitor tab** — bottom section now has Graph/Instruments tabs
- **Project system** — instrument layouts saved/loaded in .clpkg

### What it enables
Right-click signals in the Signal panel, add to Instrument Panel, get live-updating gauges, bars, and numeric displays. Cycle widget types with right-click. Layouts persist across project save/load.

### Dependencies
- Plan 1 (SignalPanelViewModel, BusDataService)
- Plan 2 (Project system for layout persistence)

---

## Plan 8: USB CDC Sideband (Future)
**Status:** NOT STARTED
**Spec section:** 2 (Future USB CDC Path)

### Scope
- Firmware USB CDC stack on RP2350
- `IStreamTransport` abstraction in config tool
- Same monitor frame format over USB serial (header+data merged into single packet)
- Automatic transport selection (USB preferred, CAN1 fallback)

### Dependencies
- Plan 4 (monitor protocol — same frame format)

---

## Progress Tracker

| Plan | Description | Status | Tests |
|------|-------------|--------|-------|
| 1 | Bus Monitor Foundation | COMPLETE | 28 |
| 2 | Project System | COMPLETE | 39 |
| 3 | LDF Parser Integration | COMPLETE | 45 |
| 4 | Firmware Monitor Protocol | COMPLETE | 117 |
| 5 | On-Board Logger + Data Logger Tab | NOT STARTED | — |
| 6 | Export Formats | COMPLETE | 61 |
| 7 | Instrument Panel | COMPLETE | 68 |
| 8 | USB CDC Sideband | FUTURE | — |
