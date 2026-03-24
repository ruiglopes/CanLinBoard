# Test Guide — CAN/LIN Gateway Board

Each phase has its own test firmware and host-side test scripts.
Tests are designed to be run **in order** — complete each phase before moving to the next.

## Prerequisites

- ARM GCC toolchain (`arm-none-eabi-gcc`) in PATH
- Pico SDK (`PICO_SDK_PATH` env var)
- FreeRTOS Kernel (`FREERTOS_KERNEL_PATH` env var)
- Python 3.8+ with `python-can` package (`pip install python-can`)
- PCAN USB adapter + Peak drivers
- can2040 submodule: `git submodule update --init`

## Quick Reference

All commands below assume you are in the `firmware/` directory:

```bash
cd firmware

# Build main firmware
cmake -B build -G Ninja && cmake --build build

# Build a test firmware
cmake --build build --target test_phase1   # or test_phase2, test_phase3, test_phase4

# Run host-side tests
python tests/phase0/test_build.py                        # No hardware needed
python tests/phase1/test_hal_host.py --channel PCAN_USBBUS1
python tests/phase2/test_can_host.py --channel PCAN_USBBUS1
python tests/phase3/test_lin_host.py --channel PCAN_USBBUS1
python tests/phase4/test_gateway_host.py --channel PCAN_USBBUS1

# Phase 7-8 use main firmware (no dedicated test firmware needed)
python tests/phase7/test_monitor_host.py --channel PCAN_USBBUS1
python tests/phase8/test_logger_host.py --channel PCAN_USBBUS1
```

---

## Phase 0: Build Verification (Host Only)

**No hardware required.** Verifies the build system, linker script, and binary structure.

```bash
python tests/phase0/test_build.py
```

| Test | What it checks | Pass criteria |
|------|---------------|---------------|
| T0.1 | Clean build | Zero errors, zero warnings |
| T0.2 | `.app_header` placement | Section at `0x10008000` in map file |
| T0.3 | Binary header fields | Magic=`0x41505001`, CRC/size patched |
| T0.4 | CRC32 correctness | `patch_header.py` CRC matches recomputed value |
| T0.5 | Vector table | SP in RAM, reset handler in flash with Thumb bit |

**Gate:** All T0.x pass before proceeding to Phase 1.

---

## Phase 1: HAL Verification (On-Target)

**Hardware:** Board + PCAN on CAN1. Optionally oscilloscope on GPIO 21.

1. Build test firmware: `cmake --build build --target test_phase1`
2. Flash `build/test_phase1.bin` via bootloader
3. Run host collector: `python tests/phase1/test_hal_host.py`
4. Reset/power the board — results appear within 5 seconds

| Test | What it checks | Automated? |
|------|---------------|-----------|
| T1.1 | CAN1 EN pin enable (LOW) | Yes |
| T1.2 | CAN1 EN pin disable (HIGH) | Yes |
| T1.3 | CAN1 TERM pin (HIGH) | Yes |
| T1.4 | CAN2 EN + TERM (4 checks) | Yes |
| T1.5 | SPI0 initialization | Yes |
| T1.6 | SJA1124 ID register read | Yes |
| T1.7 | SJA1124 register write/readback | Yes |
| T1.8 | NVM write 256 bytes | Yes |
| T1.9 | NVM readback matches | Yes |
| T1.10 | NVM erase → all 0xFF | Yes |
| T1.11 | Clock output init | Partial (scope needed for 8 MHz) |
| T1.12 | Bootloader entry | Manual (reboots board) |

**Manual verification:**
- T1.11: Measure GPIO 21 with oscilloscope → 8 MHz ±24 kHz
- T1.12: Uncomment in test_hal.c, re-flash, verify bootloader responds on CAN

**Gate:** All T1.x pass (T1.11 scope-verified) before proceeding to Phase 2.

---

## Phase 2: CAN Subsystem (On-Target + Host)

**Hardware:** Board + PCAN on CAN1. Second PCAN on CAN2 optional (enables T2.3/T2.5).

1. Build: `cmake --build build --target test_phase2`
2. Flash `build/test_phase2.bin`
3. Run: `python tests/phase2/test_can_host.py --channel PCAN_USBBUS1 [--channel2 PCAN_USBBUS2]`

| Test | What it checks | Needs 2nd PCAN? |
|------|---------------|----------------|
| T2.1 | CAN1 RX single frame | No |
| T2.2 | CAN1 TX echo data integrity | No |
| T2.3 | CAN2 RX via second adapter | Yes |
| T2.5 | Dual bus simultaneous | Yes |
| T2.6 | CAN2 initially disabled | No |
| T2.7 | CAN2 enable/re-enable | No |
| T2.8 | Config ID filtering (0x600) | No |
| T2.9 | Non-config ID routing | No |
| T2.10 | Stress test (100 frames) | No |
| T2.11 | RX latency estimation | No |
| T2.12 | Bitrate change (250 kbps) | No |

**Gate:** All T2.x pass (CAN2 tests at least via enable/disable) before Phase 3.

---

## Phase 3: LIN Subsystem (On-Target + Host)

**Hardware:** Board + PCAN on CAN1. Optionally oscilloscope on LIN bus pins.

1. Build: `cmake --build build --target test_phase3`
2. Flash `build/test_phase3.bin`
3. Run: `python tests/phase3/test_lin_host.py`

The firmware auto-runs tests on boot.

| Test | What it checks | Automated? |
|------|---------------|-----------|
| T3.1 | PLL lock with 8 MHz clock | Yes |
| T3.2 | PLL lock failure | Skipped (would disrupt state) |
| T3.3 | Channel init → Idle state | Yes |
| T3.4 | Master header TX | Yes (scope for waveform) |
| T3.5 | Master publish (8 bytes) | Yes (scope for waveform) |
| T3.6 | Slave response RX | Manual (needs external slave) |
| T3.7 | Multi-channel init (all 4) | Yes |
| T3.8 | Baud rate register accuracy | Yes |
| T3.9 | Schedule table execution | Manual (via command) |
| T3.10 | Schedule wrap | Manual |
| T3.11 | Timeout error (no slave) | Yes |
| T3.12 | Checksum error | Manual (needs corrupted response) |
| T3.13 | Channel stop/restart | Yes |
| T3.14 | Mode switch (master↔slave) | Yes |

**Manual verification:**
- T3.4/T3.5: Oscilloscope on LIN1 bus — verify frame waveform
- T3.6: Connect external LIN slave and verify DRF interrupt fires
- T3.9: Use command 0x06 via PCAN to start schedule, observe on scope

**Gate:** All automated T3.x pass, T3.4/T3.5 scope-verified.

---

## Phase 4: Gateway Engine (On-Target)

**Hardware:** Board + PCAN on CAN1. No second adapter or oscilloscope needed.

The gateway engine is the core routing and transformation layer. Phase 4 tests verify
that frames are correctly matched, transformed, and dispatched across all bus
combinations (CAN↔CAN, CAN↔LIN, LIN↔CAN) using a unified rule structure.

### Build & Flash

```bash
cmake --build build --target test_phase4
```

Flash `build/test_phase4.bin` via bootloader (same procedure as previous phases).

### Run

```bash
python tests/phase4/test_gateway_host.py --channel PCAN_USBBUS1
```

Reset/power the board after starting the host script. The firmware auto-runs all
16 tests on boot. Results appear within ~15 seconds.

### Test Architecture

The test firmware uses a **TX capture** pattern to observe routed frames without
needing a second CAN adapter:

- A `test_can_tx_task` intercepts the CAN TX queue.
- Test protocol frames (IDs 0x7F0–0x7FF) pass through to CAN hardware normally.
- Routed output frames from the engine are captured into an internal queue for
  the test orchestrator to verify.
- LIN→CAN tests inject synthetic `gateway_frame_t` directly into the gateway
  queue — no physical LIN bus needed.

**Self-receive loop prevention:** Source IDs use 0x100–0x1FF, destination IDs use
0x200–0x4FF. Rules only match the source range, so re-received output frames are
not re-routed.

### Test Matrix

| Test | What it checks | Method |
|------|---------------|--------|
| T4.1 | CAN1→CAN1 passthrough | TX capture: send 0x100, verify 0x200 with identical data |
| T4.2 | CAN1→CAN2 routing | Stats: `frames_routed ≥ 1` (CAN2 not physically started) |
| T4.3 | ID translation | TX capture: send 0x101, verify output ID = 0x300 |
| T4.4 | ID mask match (0xFF0) | TX capture: send 0x105, matches 0x100/0xFF0 rule → 0x400 |
| T4.5 | No match → drop | Stats: `frames_dropped ≥ 1`, no frame in TX capture queue |
| T4.6 | Full passthrough (0 mappings) | TX capture: all 8 bytes copied unchanged, DLC preserved |
| T4.7 | Single byte extraction | TX capture: `src[2]` → `dst[0]` (0xCC) |
| T4.8 | Mask + shift | TX capture: low nibble of 0xA5 shifted left 4 → 0x50 |
| T4.9 | CAN→LIN routing | Stats: `frames_routed ≥ 1`, `lin_tx_overflow == 0` |
| T4.10 | LIN→CAN routing | TX capture: synthetic LIN1 frame → CAN1 with ID 0x350 |
| T4.11 | Fan-out (2 rules) | TX capture: 1 frame in → 2 distinct output IDs (0x260, 0x360) |
| T4.12 | Disabled rule | TX capture: no output; stats: `frames_dropped ≥ 1` |
| T4.13 | DLC override | TX capture: input DLC=8, output DLC=4 |
| T4.14 | Multiple byte mappings | TX capture: 3 mappings (src[0]→dst[0], src[3]→dst[1], src[7]→dst[2]) |
| T4.15 | Offset mapping | TX capture: `(0x32 & 0xFF) + 10 = 0x3C` |
| T4.16 | Stats consistency | Direct inject 2 matching + 1 non-matching → `routed=2, dropped=1` |

### Pass Criteria

All 16 tests must report PASS. The host script shows per-test decoded details:

```
  [PASS] T4.1:  CAN1→CAN1 passthrough — received=1, out_id=0x200, data[0]=0x11
  [PASS] T4.2:  CAN1→CAN2 routing — routed=1, dropped=0
  ...
  [PASS] T4.16: Gateway stats consistency — routed=2, dropped=1, can_ovf=0, lin_ovf=0

  Target summary: 16/16 passed, 0 failed
```

### Troubleshooting

| Symptom | Likely cause |
|---------|-------------|
| No results received (timeout) | Board not reset, wrong channel/bitrate, test firmware not flashed |
| T4.1/T4.3/T4.4 FAIL with `received=0` | CAN self-receive not working — check CAN1 termination enabled |
| T4.2/T4.9 FAIL with `routed=0` | Frame not reaching gateway queue — check CAN RX path |
| T4.10 FAIL with `received=0` | Synthetic LIN inject not processed — check gateway task priority |
| T4.11 FAIL with only 1 of 2 frames | TX queue depth too shallow or timing issue — increase delay |
| T4.16 FAIL with wrong counts | Stale frames from prior test — flush not clearing properly |

### What This Phase Does NOT Test

- **Physical LIN TX/RX** — LIN bus wire-level verification was covered in Phase 3.
  Phase 4 focuses on the engine's routing logic, not the SJA1124 driver.
- **CAN2 physical TX** — CAN2 transceiver is not started in the test firmware.
  T4.2 verifies the engine dispatches to CAN2 via stats, not wire-level.
- **NVM persistence of rules** — Rule storage in flash is a Phase 5 (config) concern.
- **Concurrent high-throughput** — Stress testing is deferred to Phase 6 (integration).

**Gate:** All 16 T4.x pass before proceeding to Phase 5.

---

## Phase 4.5: Debug Infrastructure (On-Target + Host)

**Hardware:** Board + PCAN on CAN1. No additional hardware needed.

Phase 4.5 validates the diagnostic heartbeat, crash reporting, hardware watchdog,
and MCU telemetry. It runs in two phases: first boot (normal), then crash trigger
+ recovery validation.

### Build & Flash

```bash
cmake --build build --target test_phase4_5
```

Flash `build/test_phase4_5.bin` via bootloader.

### Run

```bash
python tests/phase4_5/test_diag_host.py --channel PCAN_USBBUS1
```

Reset/power the board after starting the host script. Phase A auto-runs on boot (~15s),
then Phase B triggers a crash via CAN command and validates recovery (~25s).

### Test Matrix

**Phase A — First Boot Validation:**

| Test | What it checks | Method |
|------|---------------|--------|
| T4.5.1 | Crash data clear on cold boot | On-target: no valid crash magic |
| T4.5.2 | CAN1 active (heartbeat source) | On-target: CAN state = ACTIVE |
| T4.5.3 | CAN stats readable (no errors) | On-target: state=ACTIVE, error_count=0 |
| T4.5.4 | Heap readable | On-target: free heap > 0 and < 48 KB |
| T4.5.5 | MCU temperature in sane range | On-target: 10–70°C |
| T4.5.6 | System state = OK | On-target: SYS_STATE_OK after boot |
| T4.5.7 | Reset reason = POWER_ON | On-target: first boot = 0x00 |
| T4.5.8 | Heap free > 1 KB | On-target: sufficient free memory |
| T4.5.9 | Stack watermark > 0 | On-target: all tasks have margin |
| T4.5.10 | Watchdog feeding (alive) | On-target: board survived to test |
| T4.5.H1–H3 | Heartbeat frames received | Host: 0x7F0/0x7F1/0x7F2 in 12s window |
| T4.5.H4 | MCU temp sane in heartbeat | Host: 0x7F0 byte 6 in 10–70°C |
| T4.5.H5 | System state in heartbeat | Host: 0x7F0 byte 4 = 0x01 |
| T4.5.H6 | Heap free in heartbeat | Host: 0x7F2 byte 6 > 0 |
| T4.5.H7 | Stack watermark in heartbeat | Host: 0x7F2 byte 7 > 0 |
| T4.5.H8 | Watchdog sustained | Host: ≥8 heartbeats in 12 seconds |

**Phase B — Crash Trigger + Recovery:**

| Test | What it checks | Method |
|------|---------------|--------|
| T4.5.H9 | Crash report 0x7F3 received | Host: frame appears after crash reboot |
| T4.5.H10 | Fault type = ASSERT_FAIL | Host: 0x7F3 byte 0 = 4 |
| T4.5.H11 | Crash PC non-zero | Host: 0x7F3 bytes 1–4 ≠ 0 |
| T4.5.H12 | Reset reason = CRASH_REBOOT | Host: 0x7F0 byte 7 = 2 |

### Crash Data Architecture

Crash data is stored in **watchdog scratch registers** [0–3], NOT in SRAM.
The bootloader's CRT0 zeroes SRAM on every boot, but watchdog scratch registers
survive all warm reboots:

- `scratch[0]` = `CRASH_DATA_MAGIC` (0xDEADFA17)
- `scratch[1]` = fault_type enum
- `scratch[2]` = PC at crash
- `scratch[3]` = LR at crash

On boot, `fault_handler_init()` reconstructs a `crash_data_t` struct from scratch
registers if the magic is valid.

### Troubleshooting

| Symptom | Likely cause |
|---------|-------------|
| T4.5.5/H4 FAIL (temp out of range) | Wrong ADC channel — must use `ADC_TEMPERATURE_CHANNEL_NUM` (8 on QFN-80) |
| T4.5.H9–H11 FAIL (no crash frame) | 0x7F3 sent during result collection, not heartbeat window — host script must capture during both |
| T4.5.H12 FAIL (reset reason wrong) | Crash data not surviving reboot — verify watchdog scratch registers used, not SRAM |
| All Phase B tests FAIL | Crash trigger not received — verify 0x7F0 command reaches gateway queue |

**Gate:** All 31 tests pass before proceeding to Phase 5.

---

## Phase 5: Configuration System (On-Target + Host)

**Hardware:** Board + PCAN on CAN1.

Phase 5 validates the config protocol (read/write params), NVM save/load,
bulk transfers (routing rules + LIN schedule tables), and runtime apply.

### Build & Flash

```bash
cmake --build build --target test_phase5
```

Flash `build/test_phase5.bin` via bootloader.

### Run

```bash
python tests/phase5/test_nvm_config_host.py --channel PCAN_USBBUS1
```

### Key Tests

| Test | What it checks |
|------|---------------|
| CONNECT handshake | FW version, config size, rule count returned |
| READ_PARAM / WRITE_PARAM | Per-section parameter read/write (CAN, LIN, Diag) |
| BULK_START / DATA / END | Routing rule bulk write + CRC verification |
| BULK_READ / BULK_READ_DATA | Routing rule bulk read with sequence counter |
| NVM save + load | Power-cycle persistence of written config |
| Runtime apply | Config changes take effect without reboot |

**Gate:** All tests pass before proceeding to Phase 6.

---

## Phase 6: Diagnostics (On-Target + Host)

**Hardware:** Board + PCAN on CAN1.

Phase 6 validates the diagnostics module: heartbeat timing, bus watchdogs,
system state machine, and configurable diagnostic parameters.

### Build & Flash

```bash
cmake --build build --target test_phase6
```

Flash `build/test_phase6.bin` via bootloader.

### Run

```bash
python tests/phase6/test_diag_host.py --channel PCAN_USBBUS1
```

### Key Tests (14 total)

| Test | What it checks |
|------|---------------|
| Heartbeat timing | 1 Hz interval on CAN1 (0x7F0, 0x7F1, 0x7F2, 0x7F4) |
| CAN bus watchdog | Timeout triggers WARN state + bitmask in health frame |
| LIN bus watchdog | Same for LIN channels |
| System state machine | OK -> WARN (low stack) -> ERROR (watchdog timeout) |
| Diag config params | Enable/disable, CAN ID, interval, bus selection |
| Watchdog reconfigure | Runtime ms change via WRITE_PARAM |

**Gate:** All 14 tests pass. All firmware phases complete.

---

## Phase 7: Bus Monitor Protocol (On-Target + Host)

**Hardware:** Board + PCAN on CAN1 + second CAN adapter or signal source on CAN2 (for multi-bus tests).

Phase 7 validates the bus monitor streaming protocol — firmware mirrors CAN/LIN frames
to the config tool on CAN IDs 0x604 (header) / 0x605 (data), with config protocol control.

**No test firmware needed** — monitor runs in the main firmware. All tests use the main
firmware binary with the config tool or Python host scripts.

### Prerequisites

- Main firmware (v0.3.0+) flashed on board
- CAN1 connected to host adapter at 500 kbps
- CAN2 connected to a second adapter or traffic source (for T7.3, T7.5, T7.8)

### Test Matrix

**A — Enable/Disable & Basic Streaming:**

| Test | What it checks | Method |
|------|---------------|--------|
| T7.1 | Monitor starts disabled | Host: `READ_PARAM(0x06, 0, 0)` returns 0 |
| T7.2 | Enable monitor | Host: `WRITE_PARAM(0x06, 0, 0, 1)`, confirm `READ_PARAM` returns 1 |
| T7.3 | CAN2 frame mirrored | Send a frame on CAN2, verify 0x604+0x605 pair on CAN1 with correct bus=1, ID, data |
| T7.4 | Sequence number increments | Send 3 frames, verify seq 0, 1, 2 in header bytes |
| T7.5 | DLC=0 header-only | Send DLC=0 frame on CAN2, verify only 0x604 received (no 0x605) |
| T7.6 | DLC=8 data[7] in header | Send 8-byte frame, verify data[0-6] in 0x605 and data[7] lower nibble in 0x604 byte 6 |
| T7.7 | Disable monitor | Host: `WRITE_PARAM(0x06, 0, 0, 0)`, send CAN2 frame, verify no 0x604/0x605 |

**B — Filtering:**

| Test | What it checks | Method |
|------|---------------|--------|
| T7.8 | Bus mask filter | Set bus mask to CAN2 only (0x02), send frames on CAN1 and CAN2, verify only CAN2 mirrored |
| T7.9 | Config frames excluded | Enable monitor with CAN1 in mask, send normal frame on CAN1 (ID 0x100) — mirrored. 0x600-0x605 never mirrored. |
| T7.10 | Diag frames excluded | Verify 0x7F0-0x7F4 heartbeat frames never appear as monitor frames |
| T7.11 | ID whitelist | Bulk write [0x123] to SectionMonitor, set filter mode=1 (whitelist), verify only ID 0x123 mirrored |
| T7.12 | ID blacklist | Set filter mode=2 (blacklist) with [0x123], verify 0x123 NOT mirrored but other IDs are |
| T7.13 | Filter mode clear | Set filter mode=0 (none), verify all IDs mirrored again |

**C — Backpressure & Priority:**

| Test | What it checks | Method |
|------|---------------|--------|
| T7.14 | Application traffic priority | Flood CAN2 frames + send config READ_PARAM, verify config response arrives within timeout |
| T7.15 | Drop count | Flood monitor queue (high-rate CAN2 traffic), read `MONITOR_PARAM_DROP_COUNT`, verify > 0 |
| T7.16 | Sequence gap on drop | During overload, verify config tool sees sequence number gaps (non-consecutive seq in headers) |

**D — LIN Monitoring (requires LIN traffic source):**

| Test | What it checks | Method |
|------|---------------|--------|
| T7.17 | LIN1 frame mirrored | Configure LIN1 master with schedule, verify monitor frames with bus=2 (LIN1) |
| T7.18 | LIN bus mask | Set bus mask to exclude LIN1, verify LIN1 frames not mirrored |

### Troubleshooting

| Symptom | Likely cause |
|---------|-------------|
| No 0x604/0x605 after enable | Monitor not enabled — check WRITE_PARAM response. Or bus mask excludes all buses. |
| 0x604 received but no 0x605 | DLC=0 frame (expected) or monitor queue overflow dropped the data frame |
| Wrong bus ID in header | Check byte 1 of 0x604 — CAN1=0, CAN2=1, LIN1=2, LIN2=3, LIN3=4, LIN4=5 |
| Config responses slow during flood | Normal — monitor TX has lower priority. Increase config timeout if needed. |
| Drop count stays 0 despite high traffic | Monitor TX queue depth (16) may be sufficient for the traffic rate. Increase send rate. |

**Gate:** T7.1-T7.16 pass. T7.17-T7.18 require LIN traffic source (can defer).

---

## Phase 8: Flash Logger (On-Target + Config Tool)

**Hardware:** Board + PCAN on CAN1 at 500 kbps. Config tool connected.

Phase 8 validates the flash data logger: manual start/stop, metadata persistence
across reboot, chunked log download, CSV export, bus mask filtering, flash error
recovery, and both continuous and triggered recording modes.

**No dedicated test firmware** — all tests use the main firmware with the config
tool's Data Logger tab and/or Python host scripts.

### Prerequisites

- Main firmware (v0.3.0+) flashed on board
- CAN1 connected to host adapter at 500 kbps
- Config tool open and connected
- Data Logger tab visible in the config tool

---

### A — Logger Foundation

| Test | What it checks | Pass criteria |
|------|---------------|---------------|
| T8.1 | Manual start/stop | Start recording, send 100 frames on 0x200, stop; verify entry count in UI matches 100 |
| T8.2 | Metadata persistence | Start/stop recording, reboot board, reconnect; verify entry count preserved (non-zero, matches pre-reboot value) |
| T8.3 | Chunked download | Record 500 frames, download via Data Logger tab; verify CRC passes and downloaded entry count matches recorded count |
| T8.4 | CSV export | Download log, export as CSV; verify only logged frame IDs present in file (no garbage rows or IDs not transmitted) |
| T8.5 | Bus mask filter | Set bus mask to CAN1 only; send frames on CAN1 and CAN2; verify only CAN1 frame IDs appear in downloaded log |
| T8.6 | Flash error recovery | Verify flash error counter is readable (initial value 0); logger stops recording after `LOG_MAX_FLASH_ERRORS` consecutive write failures |
| T8.7 | Erase all | Issue erase command from Data Logger tab; verify entry count resets to 0 and subsequent download returns empty log |
| T8.8 | CAN bus health | Start recording; verify PCAN tool shows no BUSLIGHT/BUSHEAVY events and CAN1 error count stays at 0 throughout |

---

### B — Continuous Mode

| Test | What it checks | Pass criteria |
|------|---------------|---------------|
| T8.9 | Continuous recording | Set mode to continuous, start, send frames until flash wraps; verify `wrap_count > 0` reported in logger status |
| T8.10 | Auto-resume on boot | Set continuous mode, start recording, reboot board; verify recording resumes automatically without host intervention |
| T8.11 | Gap markers | Flood logger queue beyond queue depth; verify drop counter increments in UI; download log and verify gap marker count > 0 in report |

---

### C — Triggered Mode

| Test | What it checks | Pass criteria |
|------|---------------|---------------|
| T8.12 | Arm and trigger | Set triggered mode; configure trigger (bus=CAN1, ID=0x200, op=any); arm logger; send a frame on 0x200; verify state transitions armed → capturing → idle |
| T8.13 | Pre/post trigger window | Configure `pre_trigger_kb=16` and `post_trigger_kb=16`; arm, send background traffic, send trigger frame; verify captured window size ≥ 32 KB |
| T8.14 | Trigger operators | Step through equals, greater-than, less-than, and mask operators on trigger byte; for each, verify trigger fires only on a matching byte value and does not fire on a non-matching value |
| T8.15 | Stop while armed | Arm logger; issue stop command before any trigger frame is sent; verify logger returns to idle state without capturing any data |

---

### Troubleshooting

| Symptom | Likely cause |
|---------|-------------|
| Entry count stays 0 after recording | Logger not started, or bus mask excludes all buses — check mode and mask params |
| CRC fails on download | Chunked download interrupted or reassembly out of order — retry; check for sequence gaps in download log |
| CSV contains garbage rows | Log not erased before test; entries from a prior session in flash — erase first (T8.7) |
| Auto-resume does not trigger (T8.10) | Continuous mode or auto-resume flag not persisted to NVM — issue save command before rebooting |
| Wrap count stays 0 (T8.9) | Flash not full yet — send more frames or use a smaller pre-allocated log region |
| State does not reach capturing (T8.12) | Trigger condition mismatch — verify bus, ID, and operator fields match exactly what was sent |
| Pre/post window smaller than expected (T8.13) | Log region smaller than 32 KB or pre-trigger buffer not yet full — allow more background traffic before triggering |

**Gate:** T8.1–T8.8 pass before testing continuous and triggered modes. T8.9–T8.15 can run once foundation tests pass.

---

## Test Firmware Build Targets

| Target | CMake Command | Define |
|--------|-------------|--------|
| Main firmware | `cmake --build build` | (none) |
| Phase 1 tests | `cmake --build build --target test_phase1` | `TEST_PHASE1` |
| Phase 2 tests | `cmake --build build --target test_phase2` | `TEST_PHASE2` |
| Phase 3 tests | `cmake --build build --target test_phase3` | `TEST_PHASE3` |
| Phase 4 tests | `cmake --build build --target test_phase4` | `TEST_PHASE4` |
| Phase 4.5 tests | `cmake --build build --target test_phase4_5` | `TEST_PHASE4_5` |
| Phase 5 tests | `cmake --build build --target test_phase5` | `TEST_PHASE5` |
| Phase 6 tests | `cmake --build build --target test_phase6` | `TEST_PHASE6` |
| Phase 7 tests | Main firmware + `python tests/phase7/test_monitor_host.py` | (none — uses main FW) |
| Phase 8 tests | Main firmware + `python tests/phase8/test_logger_host.py` | (none — uses main FW) |
