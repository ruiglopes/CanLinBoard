# CAN/LIN Gateway Board — Implementation Plan

## Context

Build firmware (C, FreeRTOS) for an RP2350-based CAN/LIN gateway board and a WPF (.NET) Windows configuration tool. The board bridges 2 CAN buses (via can2040) and 4 LIN channels (via SJA1124 SPI transceiver), acting as a configurable gateway. Configuration is done over CAN via a PCAN/SLCAN adapter. All settings are stored in NVM (secondary flash).

---

## 1. Architecture Overview

### Flash Memory Map

```
PRIMARY FLASH (16 Mb = 2 MB, at 0x10000000):
  0x10000000 - 0x10006FFF  Bootloader code          (28 KB)
  0x10007000 - 0x10007FFF  Bootloader config sector  (4 KB)
  0x10008000 - 0x10008100  App header (256 bytes)
  0x10008100 - 0x101FFFFF  Application code + rodata

SECONDARY FLASH (2 MB, via XIP CS1):
  Sector 0 (4 KB):  NVM Config Slot A
  Sector 1 (4 KB):  NVM Config Slot B  (ping-pong for wear leveling)
  Sector 2 (4 KB):  NVM metadata (active slot marker, write counter, CRC)
  Sector 3+:        Reserved (data logging, extended profiles)
```

Ping-pong dual-sector scheme: write to inactive slot, verify CRC, flip active marker. Provides atomic updates and ~200K write cycles.

### FreeRTOS Task Architecture

| Task | Priority | Stack | Description |
|------|----------|-------|-------------|
| `can_task` | 5 (highest) | 512w | Drains CAN RX ring buffers from both can2040 instances, handles CAN TX from outbound queue |
| `lin_task` | 4 | 512w | SJA1124 interrupt processing, LIN frame RX/TX via SPI, master scheduling engine |
| `gateway_task` | 3 | 1024w | Core routing engine — applies routing rules, byte-level transforms, posts to CAN/LIN TX queues |
| `config_task` | 2 | 512w | Handles CAN config protocol messages, reads/writes NVM, applies runtime config changes |
| `diag_task` | 1 | 512w | 4-frame heartbeat (0x7F0 status, 0x7F1 CAN stats, 0x7F2 LIN stats, 0x7F4 sys health), crash report (0x7F3), boot version frame, MCU temp, stack watermark monitoring |

**IRQ handlers** (not tasks):
- PIO0_IRQ_0: can2040 CAN1 → lock-free SPSC ring buffer → `can_task`
- PIO1_IRQ_0: can2040 CAN2 → lock-free SPSC ring buffer → `can_task`
- GPIO IRQ on pin 26: SJA1124 INTN → task notification → `lin_task`

### Inter-Task Communication

```
can2040 IRQs → [SPSC ring buffers] → can_task → [gateway_input_queue (32 deep)]
                                                         ↓
                                                   gateway_task
                                                    ↙         ↘
                             [can_tx_queue (16)] ←           → [lin_tx_queue (16)]
                                    ↓                                ↓
                               can_task                          lin_task

can_task also filters config CAN IDs → [config_rx_queue (8)] → config_task
```

---

## 2. Implementation Phases

### Phase 0: Project Scaffold ✅

**Goal:** CMake project compiles, links with FreeRTOS, boots via 2350Bootloader.

**Files to create:**
```
CanLinBoard/
  CMakeLists.txt                      # Pico SDK + FreeRTOS + can2040
  pico_sdk_import.cmake
  FreeRTOS_Kernel_import.cmake
  linker/app.ld                       # FLASH origin = 0x10008000
  include/board_config.h              # All pin definitions, constants
  include/app_header.h                # app_header_t (256 bytes, magic/version/crc/entry)
  src/main.c                          # FreeRTOS init, task creation, scheduler start
  src/app_header.c                    # Header in .app_header section
  config/FreeRTOSConfig.h             # FreeRTOS tuning
  lib/can2040/                        # git submodule
  tools/patch_header.py               # Post-build: patches size/crc32/entry_point in binary
```

**Key details:**
- Custom linker script places `.app_header` section at `0x10008000`, application code at `0x10008100`
- `PICO_BOARD=none`, `PICO_PLATFORM=rp2350-arm-s`
- Post-build Python script reads binary, computes CRC32, patches header fields to match bootloader's `app_validate()`

**Milestones:**
- [ ] M0.1: CMakeLists.txt compiles with Pico SDK + FreeRTOS (no errors, no warnings)
- [ ] M0.2: can2040 submodule integrated and compiles as static library
- [ ] M0.3: Custom linker script produces binary with `.app_header` at `0x10008000` (verified via map file)
- [ ] M0.4: `patch_header.py` correctly patches CRC32/size/entry_point in output binary
- [ ] M0.5: Binary flashes via 2350Bootloader and app_validate() passes
- [ ] M0.6: FreeRTOS scheduler starts, idle task runs (verified via GPIO toggle or debugger)

**Test Plan:**
| Test ID | Test | Method | Pass Criteria |
|---------|------|--------|---------------|
| T0.1 | Clean build | `cmake --build build` | Zero errors, zero warnings |
| T0.2 | App header placement | Inspect `.map` file | `.app_header` section at `0x10008000` |
| T0.3 | Binary header content | Hex dump first 256 bytes of `.bin` | Magic = `0x41505001`, CRC32 non-zero after patching |
| T0.4 | Bootloader validation | Flash binary, observe bootloader UART/CAN output | Bootloader reports valid app, jumps to entry point |
| T0.5 | FreeRTOS alive | Toggle an unused GPIO in idle hook, measure with scope | GPIO toggles at expected rate |

---

### Phase 1: Hardware Abstraction Layer (HAL) ✅

**Depends on:** Phase 0

**Files to create:**
```
src/hal/
  hal_gpio.h / hal_gpio.c          # CAN EN/TERM pins, LIN control pins
  hal_spi.h / hal_spi.c            # SPI driver for SJA1124 (mutex-protected)
  hal_flash_nvm.h / hal_flash_nvm.c  # Secondary flash read/write/erase
  hal_clock.h / hal_clock.c        # Clock output on GPIO 21 for SJA1124 PLL
```

**Key APIs:**
- `hal_can_enable(bus, enable)` / `hal_can_set_termination(bus, on)` — EN active-low, TERM active-high
- `hal_spi_read_reg(ctx, addr, buf, len)` / `hal_spi_write_reg(ctx, addr, data, len)` — SJA1124 SPI protocol (CPOL=0, CPHA=1, 4 MHz max, manual CS)
- `hal_nvm_read/erase_sector/write_page` — secondary flash via XIP CS1
- `hal_clock_init()` — PWM output 8 MHz on GPIO 21 for SJA1124 PLL reference
- `hal_request_bootloader()` — write SRAM magic, watchdog reset

**SPI mutex:** Protects SJA1124 SPI bus from concurrent access by `lin_task` and `config_task`.

**Milestones:**
- [ ] M1.1: All GPIO pins initialize correctly (CAN EN/TERM, LIN control)
- [ ] M1.2: SPI driver communicates with SJA1124 (read ID register returns valid silicon ID)
- [ ] M1.3: Secondary flash erase/write/read cycle completes successfully
- [ ] M1.4: 8 MHz clock output on GPIO 21 measured within +/-0.3% accuracy
- [ ] M1.5: `hal_request_bootloader()` reboots into bootloader mode

**Test Plan:**
| Test ID | Test | Method | Pass Criteria |
|---------|------|--------|---------------|
| T1.1 | CAN1 EN pin | Set enable=true, measure pin 3 | Pin reads LOW (active-low enable) |
| T1.2 | CAN1 EN pin disable | Set enable=false, measure pin 3 | Pin reads HIGH |
| T1.3 | CAN1 TERM pin | Set termination=true, measure pin 4 | Pin reads HIGH |
| T1.4 | CAN2 EN/TERM pins | Repeat T1.1-T1.3 for CAN2 pins 15/12 | Same logic, correct pins |
| T1.5 | SPI init | Initialize SPI0 at 4 MHz, CPOL=0/CPHA=1 | No hardware fault |
| T1.6 | SJA1124 ID read | Read register `0xFF` via SPI | Returns expected silicon ID byte |
| T1.7 | SJA1124 register write/read | Write MODE register, read back | Written value matches read value |
| T1.8 | NVM write | Write 256 bytes of known pattern to Slot A | No error returned |
| T1.9 | NVM readback | Read 256 bytes from Slot A | Data matches written pattern exactly |
| T1.10 | NVM erase | Erase Slot A, read back | All bytes = 0xFF |
| T1.11 | Clock output | Measure GPIO 21 with oscilloscope | Frequency = 8 MHz +/- 24 kHz |
| T1.12 | Bootloader entry | Call `hal_request_bootloader()` | Board reboots, bootloader responds on CAN |

---

### Phase 2: CAN Subsystem ✅

**Depends on:** Phase 0, Phase 1 (GPIO)

**Files to create:**
```
src/can/
  can_bus.h                          # can_frame_t, can_bus_state_t definitions
  can_manager.h / can_manager.c     # Dual can2040 init, RX/TX, ring buffers
```

**Key design:**
- CAN1 on PIO0, CAN2 on PIO1 — each with own IRQ handler and SPSC ring buffer (32 deep, power-of-2)
- can2040 callback writes to lock-free ring buffer (same pattern as bootloader `can.c`)
- `can_task` drains both ring buffers, posts frames to `gateway_input_queue` or `config_rx_queue` (if config CAN ID on CAN1)
- CAN2 can be disabled at runtime (transceiver EN pin + stop can2040 instance)
- Task notification from can2040 IRQ wakes `can_task` for <1 ms forwarding latency

**Milestones:**
- [ ] M2.1: CAN1 (PIO0) initializes at 500 kbps and receives frames
- [ ] M2.2: CAN2 (PIO1) initializes at 500 kbps and receives frames
- [ ] M2.3: Both CAN buses operate simultaneously without interference
- [ ] M2.4: CAN TX works on both buses (frames visible on external analyzer)
- [ ] M2.5: CAN2 runtime enable/disable works (transceiver + can2040 instance)
- [ ] M2.6: Received frames arrive in `gateway_input_queue` within 1 ms of bus reception
- [ ] M2.7: System stable under 100% CAN bus load for 60 seconds (no crash, ring buffer overflows counted)

**Test Plan:**
| Test ID | Test | Method | Pass Criteria |
|---------|------|--------|---------------|
| T2.1 | CAN1 RX single frame | Send 1 frame at 500 kbps from PCAN to CAN1 | Frame appears in RX ring buffer with correct ID and data |
| T2.2 | CAN1 TX single frame | Transmit 1 frame from firmware on CAN1 | Frame visible on PCAN with correct ID and data |
| T2.3 | CAN2 RX single frame | Send 1 frame from PCAN to CAN2 | Frame appears in RX ring buffer |
| T2.4 | CAN2 TX single frame | Transmit 1 frame from firmware on CAN2 | Frame visible on PCAN |
| T2.5 | Dual bus simultaneous | Send frames on both buses at 10 ms interval for 10s | All frames received on both buses, no data corruption |
| T2.6 | CAN2 disable | Disable CAN2 at runtime, send frame on CAN2 | No frame received, EN pin goes HIGH |
| T2.7 | CAN2 re-enable | Re-enable CAN2 after disable, send frame | Frame received normally, EN pin goes LOW |
| T2.8 | Config ID filtering | Send frame with ID `0x600` on CAN1 | Frame routed to `config_rx_queue`, not `gateway_input_queue` |
| T2.9 | Non-config ID routing | Send frame with ID `0x100` on CAN1 | Frame routed to `gateway_input_queue` |
| T2.10 | Stress test 100% load | Flood CAN1 at max bus rate for 60s | System alive, RX count > 0, error count tracks overflows |
| T2.11 | RX latency | Timestamp frame at IRQ, compare to task processing time | Delta < 1 ms (95th percentile) |
| T2.12 | Bitrate change | Reinitialize CAN1 at 250 kbps | Frames at 250 kbps received correctly |

---

### Phase 3: LIN Subsystem ✅

**Depends on:** Phase 1 (SPI, GPIO, clock)

**Files to create:**
```
src/lin/
  sja1124_regs.h                      # Complete register map (addresses + bit masks)
  sja1124_driver.h / sja1124_driver.c # Register-level driver (init, PLL, channel config, frame ops)
  lin_bus.h                            # lin_frame_t, lin_channel_config_t, lin_schedule_table_t
  lin_manager.h / lin_manager.c       # Channel management, scheduling engine
```

**SJA1124 driver key operations:**
- PLL configuration and lock verification (PLLIL bit in STATUS register)
- Per-channel init sequence: INIT mode → baud rate regs (LBRM/LBRL/LFR) → break/checksum config → interrupt enable → NORMAL mode
- Frame TX: write LBI (ID), LBC (DLC/DIR/CCS), LBD1-8 (data), set HTRQ in LC
- Frame RX: interrupt-driven, read LS register (DRF bit), then LBI + LBD1-8
- Baud rate calculation: `baudrate = f_pll / (16 * (IBR + FBR))`

**Master scheduling engine** (in `lin_manager_schedule_tick()`, called every 1 ms):
- For each master channel with active schedule: check if channel busy, check delay timer, load next entry, trigger header TX, advance index (wrapping)

**LIN task:** Waits on INTN GPIO interrupt notification (5 ms timeout), processes SJA1124 interrupts, runs schedule tick.

**Milestones:**
- [ ] M3.1: SJA1124 register map header (`sja1124_regs.h`) complete and reviewed against datasheet
- [ ] M3.2: SJA1124 PLL locks successfully with 8 MHz reference clock
- [ ] M3.3: Single LIN channel configured as master, transmits a header (visible on oscilloscope)
- [ ] M3.4: LIN frame TX with data bytes (master publish) works on at least one channel
- [ ] M3.5: LIN frame RX via interrupt works (slave response received and read via SPI)
- [ ] M3.6: All 4 LIN channels operational independently
- [ ] M3.7: Master scheduling engine runs a 3-entry schedule table at correct intervals
- [ ] M3.8: LIN error detection works (timeout, checksum error reported in LES register)
- [ ] M3.9: Channel mode switching (master↔slave) works at runtime

**Test Plan:**
| Test ID | Test | Method | Pass Criteria |
|---------|------|--------|---------------|
| T3.1 | PLL lock | Configure PLL with PLLMULT=0x0A, read STATUS register | PLLIL bit = 1 within 100 ms |
| T3.2 | PLL lock failure | Disable clock output, attempt PLL init | PLLIL bit = 0 after timeout, error returned |
| T3.3 | Channel init | Init LIN1 as master at 19200 baud | LSTATE register shows Normal mode |
| T3.4 | Master header TX | Trigger header for ID 0x10 on LIN1 | Oscilloscope shows break + sync + ID fields at 19200 baud |
| T3.5 | Master publish | Send header + 4 data bytes on LIN1 | Oscilloscope shows complete frame with checksum |
| T3.6 | Slave response RX | LIN1 master sends header, external slave responds | DRF interrupt fires, data read via SPI matches sent data |
| T3.7 | Multi-channel | Configure LIN1-4 as master at different baud rates | All 4 channels transmit independently without interference |
| T3.8 | Baud rate accuracy | Configure 9600 baud, measure on oscilloscope | Bit timing within +/-2% of 9600 baud |
| T3.9 | Schedule table | 3-entry table: ID 0x10 (10ms), ID 0x20 (20ms), ID 0x30 (10ms) | Headers sent at correct intervals, measured on scope |
| T3.10 | Schedule wrap | Run schedule through 3 complete cycles | Entries repeat in order, no drift > 1 ms per cycle |
| T3.11 | Timeout error | Configure master, no slave connected, send header | TOF bit set in LES register, error_count incremented |
| T3.12 | Checksum error | Inject corrupted response (if possible) | CEF bit set in LES register |
| T3.13 | Channel stop/restart | Stop LIN1, verify no activity, restart | Bus goes idle when stopped, resumes when restarted |
| T3.14 | Mode switch | Switch LIN1 from master to slave at runtime | No crash, channel in correct mode after switch |

---

### Phase 4: Gateway Engine ✅

**Depends on:** Phase 2, Phase 3

**Files to create:**
```
src/gateway/
  gateway_types.h                      # bus_id_t, gateway_frame_t, routing_rule_t, byte_mapping_t
  gateway_engine.h / gateway_engine.c  # Routing logic, frame transformation
```

**Routing rule structure:**
- Source: bus + ID + mask (for ID range matching)
- Destination: bus + ID + DLC
- Byte mappings (0-8): src_byte → dst_byte with mask/shift/offset (0 mappings = full passthrough)
- Max 32 routing rules

**Gateway task:** Blocks on `gateway_input_queue`, processes each frame against all enabled rules, posts results to `can_tx_queue` or `lin_tx_queue`.

**Latency targets:** <2 ms CAN-to-CAN, <10 ms CAN-to-LIN.

**Milestones:**
- [ ] M4.1: Routing rule data structures defined and gateway_engine compiles
- [ ] M4.2: CAN-to-CAN full passthrough works (CAN1→CAN2 and CAN2→CAN1)
- [ ] M4.3: CAN-to-LIN routing works (CAN frame triggers LIN frame on correct channel)
- [ ] M4.4: LIN-to-CAN routing works (received LIN frame forwarded as CAN frame)
- [ ] M4.5: Byte-level mapping/transformation works (mask, shift, offset)
- [ ] M4.6: ID translation works (source ID differs from destination ID)
- [ ] M4.7: Multiple rules can match the same source frame (fan-out routing)
- [ ] M4.8: CAN-to-CAN latency < 2 ms measured end-to-end

**Test Plan:**
| Test ID | Test | Method | Pass Criteria |
|---------|------|--------|---------------|
| T4.1 | CAN1→CAN2 passthrough | Rule: CAN1 ID 0x100 → CAN2 ID 0x100. Send on CAN1 | Frame appears on CAN2 with same ID and data |
| T4.2 | CAN2→CAN1 passthrough | Rule: CAN2 ID 0x200 → CAN1 ID 0x200. Send on CAN2 | Frame appears on CAN1 |
| T4.3 | ID translation | Rule: CAN1 ID 0x100 → CAN2 ID 0x300. Send 0x100 on CAN1 | Frame on CAN2 has ID 0x300 |
| T4.4 | ID mask match | Rule: CAN1 mask 0x7F0, ID 0x100. Send 0x105 on CAN1 | Matches rule (0x105 & 0x7F0 = 0x100) |
| T4.5 | No match drop | Send frame with no matching rule | No output on any bus, no error |
| T4.6 | Byte passthrough | Rule with 0 mappings, DLC=8 | All 8 data bytes copied unchanged |
| T4.7 | Byte extraction | Mapping: src_byte=2 → dst_byte=0, mask=0xFF | Destination byte 0 = source byte 2 |
| T4.8 | Bit-level mask+shift | Mapping: src=0, dst=0, mask=0x0F, shift=4 | Extracts high nibble of source byte 0 into low nibble of dest byte 0 |
| T4.9 | CAN→LIN routing | Rule: CAN1 ID 0x100 → LIN1 ID 0x10. Send on CAN1 | LIN frame with ID 0x10 transmitted on LIN1 |
| T4.10 | LIN→CAN routing | Rule: LIN1 ID 0x20 → CAN1 ID 0x200 | Received LIN frame appears as CAN frame on CAN1 |
| T4.11 | Fan-out (2 rules) | Two rules match CAN1 ID 0x100: one to CAN2, one to LIN1 | Both destinations receive the frame |
| T4.12 | Latency CAN→CAN | Timestamp at CAN1 RX IRQ, timestamp at CAN2 TX | Delta < 2 ms |
| T4.13 | Disabled rule | Set rule.enabled=false, send matching frame | No output |

---

### Phase 5: Configuration System ✅

**Depends on:** Phase 1 (NVM), Phase 2 (CAN transport)

**Files to create:**
```
src/config/
  nvm_config.h / nvm_config.c           # nvm_config_t struct, load/save/defaults/validate
  config_protocol.h                      # CAN protocol constants (IDs, commands, sections)
  config_handler.h / config_handler.c   # Command processing, config task
```

**NVM config struct** (~2 KB, packed):
- Header: magic, version, size, write_count
- CAN config [2]: bitrate, termination, enabled
- LIN config [4]: enabled, is_master, baudrate, schedule_table
- Routing table: 32 rules
- Diagnostics config: CAN ID, interval, bus
- Device profiles: WDA/CWA400 enable + channel assignment (**vestigial** — firmware stores these flags but never acts on them; profile logic is entirely in the config tool)
- CRC32 (last field)

**CAN configuration protocol:**
- `0x600` = config commands (tool → board), `0x601` = responses (board → tool), `0x602` = bulk data
- Commands: `0x01` CONNECT, `0x02` SAVE, `0x03` DEFAULTS, `0x04` REBOOT, `0x05` ENTER_BOOTLOADER, `0x06` GET_STATUS
- `0x10` READ_PARAM / `0x11` WRITE_PARAM: section + param + sub-index addressing
- `0x20`/`0x21` BULK_START/END: multi-frame transfer for routing tables and schedules (7 bytes/frame with sequence number, CRC verified)

**Sections:** 0=CAN, 1=LIN, 2=Routing (bulk only), 3=Diag, 4=Profiles, 5=Device

**Config task:** Receives filtered CAN frames from `config_rx_queue`, dispatches commands, applies changes to runtime state after SAVE.

**Milestones:**
- [ ] M5.1: `nvm_config_t` struct defined, `sizeof` < 4096 bytes
- [ ] M5.2: Default config written to NVM on first boot (virgin flash)
- [ ] M5.3: Config loads from NVM on subsequent boots with CRC validation
- [ ] M5.4: CONNECT handshake (0x01) responds with firmware version via PCAN
- [ ] M5.5: Single-parameter READ/WRITE works for all sections
- [ ] M5.6: Bulk transfer works for routing table (write + CRC verification)
- [ ] M5.7: SAVE_CONFIG persists to NVM, survives power cycle
- [ ] M5.8: LOAD_DEFAULTS resets all parameters to factory values
- [ ] M5.9: ENTER_BOOTLOADER reboots into bootloader
- [ ] M5.10: Runtime config apply works (e.g., changing CAN bitrate takes effect immediately after SAVE)

**Test Plan:**
| Test ID | Test | Method | Pass Criteria |
|---------|------|--------|---------------|
| T5.1 | First boot defaults | Erase NVM, power on | Config loaded with default values, CRC valid |
| T5.2 | Config persistence | Save config, power cycle, read back | All values match what was saved |
| T5.3 | Ping-pong slot swap | Save config twice, check meta sector | Active slot alternates between A and B |
| T5.4 | CRC validation | Corrupt 1 byte in NVM, boot | Firmware detects CRC mismatch, loads defaults |
| T5.5 | CONNECT command | Send `0x01` on CAN1 ID `0x600` | Response on `0x601` with status OK + firmware version |
| T5.6 | READ CAN1 bitrate | Send READ_PARAM(section=0, param=0, sub=0) | Response contains 500000 (default) |
| T5.7 | WRITE CAN1 bitrate | Send WRITE_PARAM(section=0, param=0, sub=0, value=250000) | Response status OK |
| T5.8 | Read after write | READ CAN1 bitrate after T5.7 | Returns 250000 |
| T5.9 | SAVE + power cycle | Send SAVE_CONFIG, power cycle, READ CAN1 bitrate | Returns 250000 (persisted) |
| T5.10 | Bulk write routing | BULK_START + 5 DATA frames + BULK_END for 3 routing rules | Response OK, CRC match |
| T5.11 | Bulk CRC mismatch | Send BULK_END with wrong CRC | Response status = CRC mismatch, rules not applied |
| T5.12 | LOAD_DEFAULTS | Send LOAD_DEFAULTS, then READ CAN1 bitrate | Returns 500000 (factory default) |
| T5.13 | REBOOT | Send REBOOT command | Board resets, CONNECT handshake succeeds after reboot |
| T5.14 | ENTER_BOOTLOADER | Send ENTER_BOOTLOADER | Board enters bootloader mode (bootloader responds on CAN) |
| T5.15 | Unknown param | Send READ_PARAM with invalid section | Response status = Unknown (0x01) |
| T5.16 | Runtime apply | Write CAN2 termination=true, SAVE | TERM pin goes HIGH immediately |

---

### Phase 6: Diagnostics ✅

**Depends on:** Phase 2, Phase 3, Phase 5

**Files to create:**
```
src/diag/
  diagnostics.h / diagnostics.c     # Bus health aggregation, diagnostic CAN message
  watchdog.h / watchdog.c           # Per-bus software watchdog timers
```

**Diagnostic CAN messages** (configurable base ID, periodic, 4 frames staggered 5ms apart):

| Frame | CAN ID | DLC | Contents |
|-------|--------|-----|----------|
| Status | base+0 | 8 | Uptime (32b BE), SysState, BusMask, MCU Temp (signed), ResetReason |
| CAN Stats | base+1 | 8 | CAN1 RX (16b BE), CAN1 Err (8b), CAN2 RX (16b BE), CAN2 Err (8b), GW Routed (16b BE) |
| LIN Stats | base+2 | 8 | LIN1-4 RX+Err (2 bytes per channel, 8b each, saturated at 255) |
| Sys Health | base+4 | 3 | HeapFree (KB), MinStackWatermark (words), WdtTimeoutMask |

Additionally, two one-shot frames are sent at boot:
- **Version frame** on base+0 (DLC=6): FW major/minor/patch, 0xAA sentinel, CRC16 (upper bytes of app CRC32)
- **Crash report** on base+3 (DLC=8, only if crash data exists): FaultType, PC (32b BE), CrashUptime (16b BE), TaskNameChar

See `docs/CanLinBoard.dbc` for the full signal-level definitions.

**Software watchdogs:** 6 FreeRTOS auto-reload timers (CAN1, CAN2, LIN1-4), configurable timeout, callback on expiry. Fed on every RX frame.

**Hardware watchdog:** RP2350 watchdog, 5s timeout, kicked from FreeRTOS idle hook.

**Diag task:** Configurable interval (default 1000ms) — sends 4-frame heartbeat, updates system state every 10 cycles. Stack: 512 words.

**Milestones:**
- [ ] M6.1: Bus health struct populated with live RX/TX/error counts from CAN and LIN managers
- [ ] M6.2: Diagnostic CAN message broadcasts at configured interval with correct byte layout
- [ ] M6.3: Per-bus software watchdog fires timeout callback when no frames received within threshold
- [ ] M6.4: Hardware watchdog kicks MCU reset if firmware hangs (verified with deliberate hang)
- [ ] M6.5: Diagnostics configurable via config protocol (CAN ID, interval, bus selection)
- [ ] M6.6: System runs stable for 1 hour under normal bus traffic with diagnostics active

**Test Plan:**
| Test ID | Test | Method | Pass Criteria |
|---------|------|--------|---------------|
| T6.1 | Diag message broadcast | Configure diag interval=100ms on CAN1, monitor with PCAN | Frames at ~10 Hz on configured CAN ID |
| T6.2 | Diag content - system OK | All buses active and healthy | Byte 0 = 0x00 (OK), Byte 1 = all status bits set |
| T6.3 | Diag content - bus error | Disconnect CAN2, wait for watchdog | Byte 1 CAN2 bit cleared, Byte 3 error count > 0 |
| T6.4 | Uptime counter | Run for 2 hours, read diagnostic frame | Byte 5 = 2 |
| T6.5 | Gateway frame counter | Route 1000 frames through gateway, read diag | Bytes 6-7 = 1000 (or rolled-over equivalent) |
| T6.6 | Software watchdog - CAN | Set CAN1 watchdog to 500ms, stop sending frames | Timeout callback fires after ~500ms, bus_state = Timeout |
| T6.7 | Software watchdog - LIN | Set LIN1 watchdog to 1000ms, no slave responses | Timeout callback fires after ~1000ms |
| T6.8 | Watchdog feed resets | Feed watchdog, verify timer resets | No timeout while frames continue arriving |
| T6.9 | Hardware watchdog test | Insert infinite loop in diag_task (debug only) | MCU resets within 5 seconds |
| T6.10 | Diag interval change | Change diag interval from 100ms to 500ms via config | Frame rate changes from ~10 Hz to ~2 Hz |
| T6.11 | Diag disable | Set diag interval to 0 | No diagnostic frames on bus |
| T6.12 | Long-term stability | Run with 2 CAN buses + 2 LIN channels active, 1 hour | No crash, memory leak, or watchdog reset |

---

### Phase 7: Device Profiles — SKIPPED (moved to Config Tool)

**Decision:** Device profiles are implemented in the Windows Config Tool (Phase 8) instead of firmware. The firmware already provides all generic primitives needed (LIN master scheduling, byte-level routing rules, NVM config persistence). Profiles are predefined combinations of LIN schedule tables + routing rules that the config tool pushes to the board. This makes it easier to add new devices without firmware updates.

---

### Phase 8: Windows Config Tool (WPF) ✅

**Depends on:** Phase 5 (protocol definition). Firmware is complete as of Phase 6.

**Project structure:**
```
software/
  CanLinConfig.sln
  TEST_GUIDE.md             ← config tool test guide
  TEST_PLAN_PROFILES.md     ← detailed profile library test plan
  CanLinConfig/
    Adapters/       ICanAdapter.cs, PcanAdapter.cs, VectorXlAdapter.cs,
                    KvaserAdapter.cs, SlcanAdapter.cs
    Protocol/       ConfigProtocol.cs, ProtocolConstants.cs
    Models/         RoutingRule.cs, LinScheduleEntry.cs, ByteMapping.cs
    Profiles/Devices/  WdaWiper.json, Cwa400Pump.json
    Services/       ConfigFileService.cs (JSON config export/import)
    Helpers/        Crc32.cs, converters
    ViewModels/     MainViewModel.cs, CanConfigViewModel.cs, LinConfigViewModel.cs,
                    RoutingViewModel.cs, ProfilesViewModel.cs, DiagConfigViewModel.cs,
                    DiagnosticsViewModel.cs (MVVM with CommunityToolkit.Mvvm)
    Views/          MainWindow.xaml, CanConfigView.xaml, LinConfigView.xaml,
                    RoutingView.xaml, ProfilesView.xaml, DiagConfigView.xaml,
                    DiagnosticsView.xaml
```

**CAN adapters:**

| Adapter | Implementation | Driver |
|---------|---------------|--------|
| PCAN | Full | Peak.PCANBasic.NET NuGet |
| Vector XL | Full | vxlapi64.dll (Vector XL Driver Library) |
| SLCAN | Full | System.IO.Ports (serial ASCII protocol) |
| Kvaser | Untested | canlib32.dll (full implementation skeleton, requires Kvaser CANlib SDK) |

**UI tabs:**
1. **CAN Config** — baud rate, termination, enable per bus
2. **LIN Config** — master/slave, baud, schedule table editor per channel
3. **Gateway Routing** — DataGrid rules editor with byte mapping sub-editor
4. **Device Profiles** — profile library browser, one-click apply, parameter controls
5. **Diagnostics** — live bus health, error counters, CAN frame monitor

**Bottom bar:** Read All / Write All / Save / Load Defaults / Enter Bootloader

#### Profile Library

Device profiles are JSON files that define the complete configuration for a specific LIN device. The config tool translates user-friendly device parameters into generic firmware config (LIN schedules + routing rules + byte mappings) and pushes them via the existing config protocol.

**Profile JSON structure:**
```json
{
  "name": "Bosch WDA Wiper Motor",
  "id": "wda_wiper",
  "version": 1,
  "lin_config": {
    "mode": "master",
    "baudrate": 19200
  },
  "schedule_table": [
    { "id": "0x20", "direction": "publish", "dlc": 4, "interval_ms": 20 },
    { "id": "0x21", "direction": "subscribe", "dlc": 4, "interval_ms": 20 }
  ],
  "parameters": [
    {
      "name": "Wiper Mode",
      "type": "enum",
      "options": ["Off", "Slow", "Fast", "Interval"],
      "frame_id": "0x20",
      "byte": 0,
      "mask": "0x07"
    },
    {
      "name": "Interval Time",
      "type": "uint8",
      "min": 0, "max": 255,
      "frame_id": "0x20",
      "byte": 1,
      "mask": "0xFF"
    }
  ],
  "can_control": {
    "rx_id": "0x300",
    "mappings": [
      { "can_byte": 0, "param": "Wiper Mode" },
      { "can_byte": 1, "param": "Interval Time" }
    ]
  },
  "can_status": {
    "tx_id": "0x301",
    "mappings": [
      { "lin_frame": "0x21", "lin_byte": 0, "can_byte": 0 },
      { "lin_frame": "0x21", "lin_byte": 1, "can_byte": 1 }
    ]
  }
}
```

**Profile apply flow:**
1. User selects a profile (e.g., "Bosch WDA Wiper") and assigns it to a LIN channel
2. Config tool generates: LIN channel config (master, 19200 baud) + schedule table entries + routing rules (CAN→LIN control, LIN→CAN status) with byte mappings
3. Config tool pushes all generated config to firmware via WRITE_PARAM + BULK_START/DATA/END
4. User clicks Save → firmware persists to NVM
5. Device operates autonomously from that point — no PC needed

**Profile UI features:**
- Profile browser with device name, description, required LIN channels
- Channel assignment dropdown (LIN1-4)
- Live parameter controls (sliders, dropdowns) that send config updates in real-time
- Optional CAN control ID assignment for headless operation
- Import/export custom profiles (JSON files)

**Milestones:**
- [x] M8.1: WPF project builds, main window with tab layout renders
- [x] M8.2: PCAN adapter connects and sends/receives CAN frames
- [x] M8.3: SLCAN adapter connects via serial port and sends/receives CAN frames
- [x] M8.4: CONNECT handshake succeeds, firmware version displayed in UI
- [x] M8.5: Read All loads every parameter from the device and populates all UI tabs
- [x] M8.6: Write All sends modified parameters back to the device
- [x] M8.7: Save button persists config to device NVM
- [x] M8.8: Routing rules editor (add/edit/delete) works with bulk transfer
- [x] M8.9: LIN schedule table editor works for all 4 channels
- [x] M8.10: Diagnostics tab shows live-updating bus health and frame monitor
- [x] M8.11: Enter Bootloader button triggers bootloader mode on device
- [x] M8.12: Profile library loads JSON profile definitions
- [x] M8.13: Applying a profile generates correct LIN config + schedule + routing rules
- [x] M8.14: Profile parameter controls update device config in real-time
- [x] M8.15: Profile import/export works (custom JSON files)

**Test Plan:**
| Test ID | Test | Method | Pass Criteria |
|---------|------|--------|---------------|
| T8.1 | PCAN connection | Select PCAN adapter, click Connect | Status shows "Connected", firmware version displayed |
| T8.2 | SLCAN connection | Select SLCAN adapter + COM port, click Connect | Status shows "Connected" |
| T8.3 | Read All | Click Read All with device connected | All parameter fields populated with device values |
| T8.4 | CAN baud rate change | Change CAN1 baud to 250K, Write All, Save | Device CAN1 now operates at 250K |
| T8.5 | CAN termination toggle | Toggle CAN1 termination checkbox, Write+Save | Termination pin changes state on hardware |
| T8.6 | LIN master/slave switch | Change LIN1 to slave mode, Write+Save | LIN1 operates as slave |
| T8.7 | LIN schedule edit | Add 3 entries to LIN1 schedule table, Write+Save | Schedule runs on device with correct entries |
| T8.8 | Add routing rule | Add CAN1→CAN2 passthrough rule via UI, Write+Save | Frames route correctly on hardware |
| T8.9 | Delete routing rule | Delete the rule from T8.8, Write+Save | Routing stops |
| T8.10 | Byte mapping editor | Open byte mapping sub-editor, add 2 mappings, save | Mappings applied correctly in gateway |
| T8.11 | WDA profile apply | Select WDA profile, assign LIN1, apply + save | LIN1 master schedule active, correct frames on bus |
| T8.12 | WDA parameter control | Change Wiper Mode to "Fast" via profile UI | LIN frame data byte updated on device |
| T8.13 | CWA400 profile apply | Select CWA400, assign LIN2, apply + save | LIN2 schedule active with pump frames |
| T8.14 | Profile CAN control | Configure CAN control ID, send CAN frame | Device parameter changes via CAN→LIN routing |
| T8.15 | Profile persistence | Apply profile, save, power cycle device | Profile config survives reboot |
| T8.16 | Profile channel conflict | Apply two profiles to same LIN channel | UI prevents or warns about conflict |
| T8.17 | Profile hot-swap | Remove WDA from LIN1, apply CWA400 to LIN1 | Clean switch, no leftover schedule entries |
| T8.18 | Custom profile import | Import a user-created JSON profile | Profile appears in library, can be applied |
| T8.19 | Diagnostics display | Open Diagnostics tab with active buses | Bus health updates in real-time (~1 Hz) |
| T8.20 | CAN frame monitor | Open monitor, send frames on bus | Frames appear in log with timestamp, ID, data |
| T8.21 | Load Defaults | Click Load Defaults | All fields reset to factory values |
| T8.22 | Enter Bootloader | Click Enter Bootloader | Device enters bootloader, tool shows disconnected |
| T8.23 | Disconnect handling | Unplug CAN adapter during session | UI shows "Disconnected", no crash |
| T8.24 | Timeout handling | Send command, device unpowered | Timeout error displayed after 500ms, no hang |

---

## 3. Risk Areas

| Risk | Mitigation |
|------|------------|
| Dual can2040 PIO instances on RP2350 | Test in Phase 2 immediately. Can2040 should work on PIO1 but verify IRQ numbering. |
| Secondary flash XIP CS1 access | Test in Phase 1. Fallback: use tail of primary flash for NVM. |
| SJA1124 SPI timing (2 us processing delay) | Guard time between transactions. Mutex + task context naturally adds delay. |
| can2040 callback runs in PIO IRQ — must use `FromISR` variants | Lock-free SPSC ring buffer + `vTaskNotifyGiveFromISR()` only. |
| SJA1124 PLL lock depends on clock output stability | Init clock before SJA1124. Poll PLLIL with 100 ms timeout. |
| NVM config struct growth past 4 KB sector | Currently ~2 KB. Monitor size. Multi-sector fallback if needed. |
| LIN master schedule timing jitter | SJA1124 handles frame bit-level timing autonomously; MCU only triggers headers. Priority 4 task. |

---

## 4. Implementation Order

```
Phase 0 ✅ → Phase 1 ✅ → Phase 2 ✅ → Phase 3 ✅ → Phase 4 ✅ → Phase 4.5 ✅ → Phase 5 ✅ → Phase 6 ✅ → Phase 8 ✅
                                                                                               (Phase 7 SKIPPED — profiles moved to config tool)
```

**All phases complete.** Firmware (Phases 0–6) tested on-target. Windows Config Tool (Phase 8) fully implemented with PCAN, SLCAN, Vector XL adapters, profile library, and all 15 milestones verified.

**Post-completion hardening (2026-03-16):** All 6 P0 critical bugs fixed and verified on-target. See `docs/ROADMAP.md` §1 for details.
