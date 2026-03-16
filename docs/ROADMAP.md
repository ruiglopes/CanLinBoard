# CAN/LIN Gateway Board — Roadmap

Comprehensive audit performed 2026-03-15 covering firmware, config tool, tests, documentation, and bootloader compatibility.

---

## 1. Critical Bugs ~~(fix before next release)~~ ALL FIXED (2026-03-16)

### ~~1.1 Bootloader entry without unlock key via config protocol~~ FIXED
**File:** `firmware/src/config/config_handler.c`
`handle_enter_bootloader()` now requires `dlc >= 5` with valid unlock key. Frames without the key are rejected with `CFG_STATUS_INVALID_PARAM`.
**Verified:** On-target regression test (`tests/test_bug_1_1.py`) — 2/2 passed.

### ~~1.2 Diagnostics can transmit on disabled CAN2~~ FIXED
**File:** `firmware/src/can/can_manager.c`
`can_manager_transmit()` now checks `can_stats[bus].state == CAN_STATE_ACTIVE` before accessing the can2040 instance. Returns `false` for inactive buses.
**Verified:** On-target regression test (`tests/test_bug_1_2.py`) — 2/2 passed.

### ~~1.3 Zero bitrate causes divide-by-zero~~ FIXED
**File:** `firmware/src/config/config_handler.c`
WRITE_PARAM handler now validates CAN bitrate (10,000–1,000,000) and LIN bitrate (1,000–20,000). Out-of-range values rejected with `CFG_STATUS_INVALID_PARAM`.
**Verified:** On-target regression test (`tests/test_bug_1_3.py`) — 8/8 passed.

### ~~1.4 `apply_config` does not stop CAN2 before restarting~~ FIXED
**File:** `firmware/src/config/config_handler.c`
`apply_config()` now calls `can_manager_stop_can2()` unconditionally before starting CAN2, matching the LIN pattern (stop-then-start).
**Verified:** On-target regression test (`tests/test_bug_1_4.py`) — 4/4 passed.

### ~~1.5 Config tool: Remove Mapping and Move Up/Down buttons are non-functional~~ FIXED
**Files:** `software/CanLinConfig/ViewModels/RoutingViewModel.cs`, `LinConfigViewModel.cs`, `Views/RoutingView.xaml`, `Views/LinConfigView.xaml`
Added `SelectedMapping` and `SelectedEntry` properties with `SelectedItem` DataGrid bindings. Commands changed to parameterless, using selected properties (consistent with existing `SelectedBitMapping` pattern).
**Verified:** Manual UI testing.

### ~~1.6 Config tool: Profile mask/shift not applied correctly for non-zero-aligned masks~~ FIXED
**File:** `software/CanLinConfig/ViewModels/ProfilesViewModel.cs`
`GetByteValue()` now shifts the value left by the mask's LSB position before applying the mask.
**Verified:** Manual UI testing.

---

## 2. Important Issues (fix soon)

### 2.1 SJA1124 init failure silently ignored
**File:** `firmware/src/lin/lin_manager.c:287`
If `sja1124_init()` fails, the LIN task continues and tries to start channels on a non-initialized SPI context, causing cascading SPI errors.
**Fix:** Log the failure and skip channel initialization if `sja1124_init()` returns error.

### 2.2 SJA1124 PLL loss-of-lock not recovered
**File:** `firmware/src/lin/lin_manager.c:153`
`s_pll_lost_lock` is set by the interrupt handler but nothing reads it or takes corrective action. LIN communication silently fails after a PLL glitch.
**Fix:** Check `s_pll_lost_lock` in the LIN task loop and attempt re-initialization.

### 2.3 `sec_flash_wait_busy` has no timeout
**File:** `firmware/src/hal/hal_flash_secondary.c:149-152`
Loops forever waiting for flash busy bit. A malfunctioning flash chip freezes the system with the bus acquired until the HW watchdog fires (5s).
**Fix:** Add an iteration counter and return an error status after a reasonable timeout.

### 2.4 `handle_bulk_read_data` can loop forever on CAN bus-off
**File:** `firmware/src/config/config_handler.c:644-646`
`while (!can_manager_transmit(...)) { vTaskDelay(1); }` retries indefinitely. A CAN bus fault during bulk read permanently stalls the config handler task.
**Fix:** Add a retry limit or timeout.

### 2.5 No FreeRTOS resource creation failure checks
**File:** `firmware/src/main.c:129-132`
`xQueueCreate` and `xTimerCreate` return values are not checked. NULL handles cause HardFault on first use if heap is exhausted.
**Fix:** Check return values and halt with a diagnostic message.

### 2.6 Config read across tasks without synchronization
**File:** `firmware/src/config/config_handler.c:712-715`
`config_handler_get_config()` returns a pointer to `s_working_config` that is read by diagnostics/gateway/LIN tasks while the config task modifies it. Multi-byte fields can be read in a torn state.
**Fix:** Use a mutex or double-buffering for config reads.

### 2.7 Config tool: File I/O has no error handling
**File:** `software/CanLinConfig/Services/ConfigFileService.cs:212-227`
`LoadFromFile` / `SaveToFile` have no try/catch. Malformed JSON or permission errors crash the application.

### 2.8 Config tool: Profile storage path is not user-writable
**File:** `software/CanLinConfig/ViewModels/ProfilesViewModel.cs:317`
Profiles are stored in `AppDomain.CurrentDomain.BaseDirectory`. If installed under Program Files, writes throw `UnauthorizedAccessException`.
**Fix:** Use `%AppData%/CanLinConfig/Profiles/`.

### 2.9 Config tool: `BulkWriteAsync` retry does not check second send result
**File:** `software/CanLinConfig/Protocol/ConfigProtocol.cs:247-249`
If the first `Send()` fails, it retries once without checking the retry return value. Silent data loss leads to CRC mismatch at BULK_END.

---

## 3. Robustness Improvements

### 3.1 Firmware input validation
- Validate CAN bitrate range (10000–1000000) in WRITE_PARAM handler
- Validate LIN bitrate range (1000–20000) in WRITE_PARAM handler
- Validate CAN ID range (0x000–0x7FF) for diagnostic config
- Prevent disabling CAN1 via config protocol (would brick config access)
- Bounds-check `bus` parameter in `can_manager_transmit()` and `can_manager_get_stats()`

### 3.2 Config tool input validation
- Validate bitrate values in CAN config UI (reject 0, out-of-range)
- Validate CAN ID range in diagnostic config (0x000–0x7FF, warn on protocol ID conflicts)
- Validate DLC and byte index ranges in routing rule editor

### 3.3 CAN error recovery
- Detect CAN bus-off state and attempt automatic recovery after a delay
- Report bus-off events via diagnostic CAN message

### 3.4 NVM write safety
- Add a timeout to `sec_flash_wait_busy()` (e.g., 100ms per page operation)
- Consider releasing the QMI bus between page writes to allow CAN IRQs to fire

### 3.5 Config tool adapter resilience
- Mark `_connected` as `volatile` in PCAN and Kvaser adapters (read from RX thread, written from UI thread)
- Fix VectorXlAdapter disconnect race condition (join RX thread before closing port)
- Call `xlCloseDriver()` on application exit
- Verify SLCAN command responses in `ConnectAsync`

---

## 4. Test Coverage

### 4.1 Add CI pipeline
- GitHub Actions workflow: build firmware (Phase 0 tests), build config tool (`dotnet build`)
- No hardware required — runs on every push
- Emit JUnit XML from Python test scripts for CI integration

### 4.2 Config tool unit tests
Create `software/CanLinConfig.Tests/` with xUnit. Priority targets:
- `Crc32` — trivial, high confidence baseline
- `DbcParser` / `LdfParser` — complex parsers, high bug risk
- `ConfigFileService` — JSON round-trip verification
- `RoutingRule.Serialize()` — cross-verify against firmware struct sizes
- `ConfigProtocol` — binary serialization correctness

### 4.3 Missing firmware test scenarios
- BULK_READ path (0x22 / 0x23) — not tested in any phase
- Gateway rule capacity limit (fill `MAX_ROUTING_RULES`, add one more)
- Byte mapping edge cases (shift-right, offset wrapping, out-of-bounds byte index)
- Config protocol error sequences (BULK_DATA before BULK_START, duplicate BULK_START)
- CAN error recovery (bus-off, error passive)
- NVM corruption recovery (both slots invalid, metadata sector corruption)
- `apply_config()` during active gateway traffic (regression test for race fixes)
- All 4 LIN channels active simultaneously with concurrent schedules

### 4.4 Integration test (firmware + config tool)
- Automated test that uses the config tool's `ConfigProtocol` class over a real CAN adapter
- Exercises: connect, read all params, write rules, bulk transfer, read back, verify round-trip
- Does not require CANoe (unlike the existing CAPL test)

### 4.5 Fix stale test references
- ~~`firmware/tests/TEST_GUIDE.md` references `test_config_host.py` but actual file is `test_nvm_config_host.py`~~ DONE
- Phase 5 BULK_START format may be outdated (old format vs new `sub` field)

---

## 5. Documentation

### 5.1 User documentation
- ~~Config tool user guide (getting started, connecting, configuring, profiles)~~ DONE — `software/USER_GUIDE.md`
- ~~Profile JSON format specification~~ DONE — included in USER_GUIDE.md
- Hardware setup guide (wiring, CAN termination, LIN connections) — still needed

### 5.2 Stale documentation
- ~~CLAUDE.md: Kvaser described as "P/Invoke declarations only" but has full implementation skeleton~~ FIXED
- ~~`docs/implementation-plan.md`: describes single 8-byte diagnostic frame but firmware sends 4 frames~~ FIXED
- ~~NVM `profiles` section vestigial — document that profiles are config-tool-side only~~ FIXED
- ~~`software/TEST_GUIDE.md`: Kvaser described as "stub only"~~ FIXED

### 5.3 DBC gaps
- ~~Add bootloader CAN IDs: 0x701 (BL_Response), 0x702 (BL_Data), 0x7FF (BL_Debug)~~ DONE
- ~~Document the boot-time version frame on 0x7F0 (different layout from heartbeat)~~ DONE

---

## 6. Future Features

### 6.1 Bootloader compatibility (forward-looking)

**HMAC firmware signing**
The bootloader now supports `fw_hmac` at header offset 0x14 (32 bytes, HMAC-SHA256). Current firmware fills this with 0xFF (unsigned). To support signed firmware:
- Add `fw_hmac[32]` field to `app_header_t` (shrink `reserved` from 236 to 204)
- Update `patch_header.py` to accept `--key <keyfile>` and compute HMAC-SHA256
- Add signing step to build process

**29-bit extended CAN ID support**
The bootloader supports `can_id_mode` config parameter. If set to extended (29-bit), the bootloader uses 29-bit frames for 0x700/0x701/0x702/0x7FF. The firmware's `check_bootloader_cmd()` currently filters on standard 11-bit IDs only. To support mixed deployments:
- Read the bootloader's `can_id_mode` at startup (or mirror it in app NVM config)
- Adjust `check_bootloader_cmd()` to match extended frames when configured
- Support extended IDs in routing rules and config protocol

### 6.2 CAN1 runtime bitrate change
Currently CAN1 bitrate only takes effect at boot. Implementing runtime change requires:
- Add `can_manager_stop_can1()` / `can_manager_restart_can1()`
- After changing bitrate, send a "bitrate changing" response, wait for TX drain, stop, reconfigure, restart
- Config tool must reconnect at the new bitrate
- Risk: if the new bitrate is wrong, the device becomes unreachable until reboot

### 6.3 CAN bus "bridge all" mode
A one-button mode that mirrors all CAN1 traffic to CAN2 and vice versa without requiring individual routing rules. Useful for initial setup, debugging, and simple passthrough deployments.

### 6.4 Data logging
The secondary flash (16 MB W25Q128) has ample space beyond the 12 KB NVM region. A ring-buffer logger could:
- Record timestamped CAN/LIN frames to flash
- Support start/stop via config protocol
- Allow bulk readback to the config tool
- Configurable filters (which IDs to log)

### 6.5 Firmware update via config tool
The config tool should be able to flash new firmware to the board over CAN, leveraging the existing 2350Bootloader. This requires:
- A "Firmware Update" UI page or dialog with file picker for `.bin` files
- Validate the binary (check app header magic, CRC32, size limits)
- Reboot the device into bootloader mode (reuse existing `RebootToBootloaderAsync`)
- Implement the bootloader's CAN flash protocol (erase, write pages via 0x700/0x702, verify)
- Show progress bar during transfer
- Verify flashed image (read-back CRC or bootloader verify command)
- Automatically reboot the device back into application mode after successful update
- Handle errors gracefully (timeout, CRC mismatch, bus-off) with retry/abort options
- Consider supporting firmware files bundled with the config tool for "one-click" updates

### 6.6 Config tool UI improvements

**Logging infrastructure**
Add file-based logging (e.g., Serilog) for adapter errors, protocol traces, and CRC mismatches. Currently all errors are swallowed or shown only in the status bar.

**Progress indication for bulk transfers**
Add progress callbacks to `BulkWriteAsync` / `BulkReadAsync`. Show a progress bar during large transfers (2KB+ routing rules).

**Busy state management**
Disable action buttons during async operations. Add a spinner or progress ring. Prevent double-clicks on Write All / Save NVM.

**Per-section read/write**
Allow reading or writing individual config sections (CAN, LIN, Routing, Diagnostics) instead of only Read All / Write All.

**Reboot button**
`ConfigProtocol.RebootAsync()` is implemented but no UI button exposes it. Add a Reboot button alongside Enter Bootloader.

**Frame monitor auto-scroll**
The diagnostics frame log DataGrid does not auto-scroll. Add auto-scroll with a toggle to pause.

**Undo/redo**
Allow undoing configuration changes before writing to device.

### 6.7 Advanced gateway features

**Conditional routing**
Route frames based on data content (e.g., only forward if byte 0 > threshold). Currently routing is based only on CAN ID and mask.

**Frame rate limiting**
Limit the forwarding rate of specific rules to prevent bus flooding.

**J1939 / ISO-TP support**
Multi-frame protocol support for larger payloads.

**Gateway statistics per rule**
Track match count, last match timestamp, and error count per routing rule (currently only aggregate stats).

### 6.8 Fleet / multi-device management

**Device serial number / unique ID**
Read the RP2350's unique chip ID and expose it via the config protocol. Useful for managing multiple boards on the same bus.

**Configuration profiles for the device (not just LIN profiles)**
Save/restore complete device configurations (all CAN, LIN, routing, diagnostic settings) as named profiles in the config tool. Currently only LIN device profiles (WDA, CWA400) are supported.

### 6.9 Security hardening

**Config protocol authentication**
Add a challenge-response handshake before allowing write operations. Currently any node on CAN1 can read and write all configuration.

**NVM config encryption**
Encrypt sensitive configuration data in NVM to prevent extraction via flash dumps.

---

## 7. Code Quality

### 7.1 Firmware cleanup
- Remove dead variable `s_can_task_handle` (`can_manager.c:61`)
- Remove dead variable `s_can_tx_queue` (`config_handler.c:25`)
- Consolidate `can_bus_id_t` and `bus_id_t` enums to prevent accidental cross-use
- Add `s_bulk_buffer` size comment noting it's shared between routing rules and LIN schedules
- Extract magic numbers in crash report frame construction to named constants
- Populate remaining `crash_data_t` fields in HardFault handler (CFSR, HFSR, PSP, MSP)

### 7.2 Config tool cleanup
- Consolidate duplicate converter classes into `Helpers/Converters.cs`
- Remove dead properties (`RoutingRule.SrcBusName`, `DstBusName`, etc.)
- Move model classes out of `ProfilesViewModel.cs` into `Models/`
- Fix typo `ToByteMapppings` (three p's) in `BitMapping.cs:12`
- Remove misleading `[JsonIgnore]` on `RoutingRule.ProfileTag`

---

## Priority Matrix

| Priority | Category | Items |
|----------|----------|-------|
| ~~**P0 — Now**~~ | ~~Critical bugs~~ | ~~1.1, 1.2, 1.3, 1.4, 1.5, 1.6~~ ALL FIXED |
| **P1 — Next sprint** | Important issues | 2.1–2.9 |
| **P2 — Near-term** | Robustness | 3.1–3.5 |
| **P2 — Near-term** | Test coverage | 4.1, 4.2, 4.5 |
| **P2 — Near-term** | Documentation | 5.1, 5.2, 5.3 |
| **P3 — Medium-term** | Test coverage | 4.3, 4.4 |
| **P3 — Medium-term** | Code quality | 7.1, 7.2 |
| **P3 — Medium-term** | Features | 6.1 (HMAC), 6.3, 6.5 (FW update), 6.6 |
| **P4 — Long-term** | Features | 6.2, 6.4, 6.7, 6.8, 6.9 |
