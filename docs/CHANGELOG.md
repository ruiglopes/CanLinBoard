# Changelog

All notable changes to the CAN/LIN Gateway Board project.

---

## v0.2.2 — 2026-03-22

All P1 bugs resolved.

### Fixed
- **Config tool file I/O error handling** — `SaveToFile`/`LoadFromFile` wrapped in try/catch with MessageBox error dialogs for malformed JSON, permission errors, and other exceptions
- **Profile storage path** — moved from `AppDomain.BaseDirectory` to `%AppData%/CanLinConfig/Profiles/`. Bundled profiles auto-copied on first run. Fixes `UnauthorizedAccessException` when installed under Program Files
- **BulkWriteAsync retry** — retry `Send()` return value now checked; returns failure status instead of silently losing data (which caused confusing CRC mismatch at BULK_END)
- **Heartbeat timing drift** — `vTaskDelay` replaced with `vTaskDelayUntil` in diagnostics task (fixed 15ms drift from 1015ms to exact 1000ms)

---

## v0.2.1 — 2026-03-22

Config thread safety fix.

### Fixed
- **Config read across tasks without synchronization** — added FreeRTOS mutex with priority inheritance to protect `s_working_config`. All multi-byte writes (bitrate, CAN ID, interval, routing rules, LIN schedules) locked in CONFIG task. Reader tasks (DIAG, LIN, GATEWAY) lock and copy to locals before use. Refactored `send_heartbeat()` to take extracted values instead of raw config pointer. Verified on-target: Phase 5 (15/15), Phase 6 (14/14), 30s heartbeat sanity, concurrent write stress test.

---

## v0.2.0 — 2026-03-22

Firmware update via config tool — flash firmware over CAN using the bootloader protocol.

### Added
- **Firmware Update dialog** — modal window launched from "Update Firmware" button in bottom bar
- **Bootloader protocol integration** — uses `lib/CanBus.*` libraries (git subtree from 2350Bootloader)
- **File format support** — `.bin` (single-bank, always Bank A) and `.dfw` (dual-bank container with per-bank images)
- **HMAC authentication** — key file or hex input, in-memory signing of unsigned binaries
- **Delta updates** — sector CRC comparison, only flashes changed sectors
- **Transfer resume** — detects interrupted transfers, resumes from last page boundary
- **Bitrate auto-scan** — user-selectable bootloader bitrate with fallback scan through 125k/250k/500k/1M
- **Dual-bank flashing** — auto-detects active bank, flashes inactive bank, switches on success (`.dfw` only)
- **Progress reporting** — per-stage progress bar, byte/sector counters, timestamped log
- **Device identity** — logs UID, flash JEDEC IDs, CAN bus diagnostics on connect
- **Adapter handoff** — disconnects config tool adapter, uses lib adapter for bootloader, reconnects after
- **Settings persistence** — HMAC key path and bootloader bitrate saved to `%AppData%/CanLinConfig/`

### Changed
- Removed "Enter Bootloader" button from bottom bar (firmware updater handles this internally)
- Upgraded `Peak.PCANBasic.NET` NuGet from 4.9.0.942 to 4.10.1.968

### New Files
- `Models/AppHeader.cs`, `Models/DfwContainer.cs` — firmware binary parsers
- `Services/AdapterFactory.cs` — maps config tool adapters to bootloader lib adapters
- `Services/FirmwareUpdateService.cs` — flash workflow orchestrator
- `Services/FirmwareUpdateSettings.cs` — persisted dialog preferences
- `ViewModels/FirmwareUpdateViewModel.cs` — dialog MVVM logic
- `Views/FirmwareUpdateWindow.xaml` — dialog UI

---

## v0.1.2 — 2026-03-16

P1 important issue fixes — robustness and error recovery.

### Fixed
- **SJA1124 init failure silently ignored** — `lin_task_entry()` now checks `sja1124_init()` return value, marks all channels `LIN_STATE_ERROR` on failure, and rejects subsequent `start_channel` calls (`lin_manager.c`)
- **SJA1124 PLL loss-of-lock not recovered** — LIN task main loop detects PLL loss, stops all channels, resets SJA1124, re-initializes, and restarts enabled channels from NVM config (`lin_manager.c`)
- **`sec_flash_wait_busy` has no timeout** — now takes `max_polls` parameter, page program 100k polls, sector erase 500k polls. Return value propagated through all callers (`hal_flash_secondary.c`)
- **`handle_bulk_read_data` can loop forever on CAN bus-off** — bulk read TX retry loop now has configurable `diag.bulk_tx_retries` (default 50) and `diag.bulk_tx_retry_delay_ms` (default 1), exposed as diag params 6/7 (`config_handler.c`)
- **No FreeRTOS resource creation failure checks** — added `ASSERT_ALLOC` macro applied to all `xQueueCreate` and `xTaskCreate` calls; failures save crash data and reboot (`main.c`)

---

## v0.1.1 — 2026-03-16

P0 critical bug fixes — security, stability, and UI. All fixes verified on-target with regression tests.

### Fixed
- **Bootloader entry without unlock key** — `handle_enter_bootloader()` now requires `dlc >= 5` with valid unlock key; rejects frames without key (`config_handler.c`)
- **Diagnostics can transmit on disabled CAN2** — `can_manager_transmit()` checks `can_stats[bus].state == CAN_STATE_ACTIVE` before accessing can2040 instance (`can_manager.c`)
- **Zero bitrate causes divide-by-zero** — WRITE_PARAM validates CAN bitrate 10,000–1,000,000 and LIN bitrate 1,000–20,000 (`config_handler.c`)
- **`apply_config` does not stop CAN2 before restarting** — now calls `can_manager_stop_can2()` unconditionally before starting CAN2 (`config_handler.c`)
- **Config tool: routing UI Remove/Move buttons non-functional** — added `SelectedMapping`/`SelectedEntry` properties with proper DataGrid bindings (`RoutingViewModel.cs`, `LinConfigViewModel.cs`)
- **Config tool: profile mask/shift not applied for non-zero-aligned masks** — `GetByteValue()` now shifts value left by mask's LSB position (`ProfilesViewModel.cs`)

---

## v0.1.0 — 2026-03-09

Initial feature-complete release. All firmware phases implemented and tested on-target. Windows config tool fully functional.

### Firmware
- **Phase 0: Project Scaffold** — CMake + FreeRTOS + can2040, bootloader-compatible linker script, post-build header patching (18/18 build tests pass)
- **Phase 1: HAL** — GPIO, SPI (SJA1124), secondary flash NVM, 8 MHz clock output, bootloader entry (11/11 on-target tests pass)
- **Phase 2: CAN Subsystem** — Dual can2040 on PIO0/PIO1, lock-free SPSC ring buffers, CAN2 runtime enable/disable, config ID filtering, stress tested (all on-target tests pass)
- **Phase 3: LIN Subsystem** — SJA1124 driver (register-level), PLL lock, 4-channel master/slave, scheduling engine, baud rate config, error handling (9/9 on-target tests pass)
- **Phase 4: Gateway Engine** — Routing rules, ID translation, mask matching, byte-level mapping (mask/shift/offset), fan-out, DLC override, CAN-LIN bridging (16/16 on-target tests pass)
- **Phase 4.5: Debug Infrastructure** — HardFault handler (naked ASM trampoline), crash data in watchdog scratch registers, HW watchdog (5s), configASSERT override, 3-frame heartbeat, MCU temperature (31/31 on-target tests pass)
- **Phase 5: Configuration System** — NVM config with ping-pong dual-sector, CAN config protocol, bulk transfer with CRC, runtime config apply, bootloader entry with unlock key (all on-target tests pass)
- **Phase 6: Diagnostics** — 4-frame heartbeat (0x7F0–0x7F4), boot version frame, crash report, per-bus software watchdogs (6 timers), bus watchdog enable/disable tied to config apply (14/14 on-target tests pass)
- **Phase 7: Skipped** — device profiles moved to config tool

### Windows Config Tool (Phase 8)
- WPF .NET 8 with MahApps.Metro Dark theme and CommunityToolkit.Mvvm
- CAN adapters: PCAN (full), Vector XL (full), SLCAN (full), Kvaser (untested skeleton)
- Config protocol: read/write params, bulk transfer with CRC, runtime apply
- UI tabs: CAN Config, LIN Config, Gateway Routing, Diagnostics Settings, Live Diagnostics, Profiles
- Profile library: WDA wiper + CWA400 pump, JSON-based, import/export
- DBC/LDF parser for profile import (code complete, untested)
- Profile editor with 4-tab UI (General, Schedule, CAN Mappings, Parameters)
- Config file export/import (JSON)
- Runtime sizeof query for struct size verification

### Critical Architecture Decisions
- FreeRTOS `configRUN_FREERTOS_SECURE_ONLY = 1` required for RP2350 (no TrustZone)
- PIO IRQ priority 0 — above BASEPRI, never masked by FreeRTOS critical sections
- No FreeRTOS API calls in can2040 callbacks (lock-free ring buffer only)
- `can2040_transmit()` wrapped with `irq_set_enabled(false/true)` for re-entrancy protection
- NVM on secondary flash CS1 with QMI direct-mode driver
- `nvm_config_save`/`nvm_config_load` use static buffers (stack was too small for ~3161 byte config struct)
- LIN routed frames to master channels update schedule entry data instead of transmitting directly
- `gateway_engine_replace_rules()` uses critical section for atomic rule table swap

### Bug Fixes (pre-release)
- FreeRTOS `configRUN_FREERTOS_SECURE_ONLY` crash fix (root cause of board hang at boot)
- Linker script rewrite (removed unnecessary `no_flash`, based on working bootloader test-app)
- `apply_config()` crash guards for uninitialized CAN2/LIN subsystems
- NVM config stack overflow fix (static buffers for save/load)
- Boot-time config apply for LIN and gateway (was only applying CAN)
- Cross-task race fixes in `apply_config()` (critical sections, atomic rule replacement)
- LIN schedule partial memcpy fix (memset before copy)
- LIN router vs scheduler conflict resolution
- Various build warning fixes
