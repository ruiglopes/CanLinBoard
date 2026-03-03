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

## Test Firmware Build Targets

| Target | CMake Command | Define |
|--------|-------------|--------|
| Main firmware | `cmake --build build` | (none) |
| Phase 1 tests | `cmake --build build --target test_phase1` | `TEST_PHASE1` |
| Phase 2 tests | `cmake --build build --target test_phase2` | `TEST_PHASE2` |
| Phase 3 tests | `cmake --build build --target test_phase3` | `TEST_PHASE3` |
| Phase 4 tests | `cmake --build build --target test_phase4` | `TEST_PHASE4` |
