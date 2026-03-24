# CAN/LIN Gateway Board — Roadmap

Outstanding work organized by priority. For completed work history, see [CHANGELOG.md](CHANGELOG.md).

---

## P2 — Robustness (now highest priority)

### 5. NVM write safety
Flash timeout is implemented. Remaining:
- Consider releasing QMI bus between page writes to allow CAN IRQs to fire

### 6. Config tool adapter resilience
- Mark `_connected` as `volatile` in PCAN and Kvaser adapters (read from RX thread, written from UI thread)
- Fix VectorXlAdapter disconnect race condition (join RX thread before closing port)
- Call `xlCloseDriver()` on application exit
- Verify SLCAN command responses in `ConnectAsync`

### 7. CI pipeline
- GitHub Actions workflow: build firmware (Phase 0 tests), build config tool (`dotnet build`)
- No hardware required — runs on every push
- Emit JUnit XML from Python test scripts for CI integration

### 8. Config tool unit tests
Create `software/CanLinConfig.Tests/` with xUnit. Priority targets:
- `Crc32` — trivial, high confidence baseline
- `DbcParser` / `LdfParser` — complex parsers, high bug risk
- `ConfigFileService` — JSON round-trip verification
- `RoutingRule.Serialize()` — cross-verify against firmware struct sizes
- `ConfigProtocol` — binary serialization correctness

### 9. Fix stale test references
- Phase 5 BULK_START format may be outdated (old format vs new `sub` field)

---

## P3 — Quality & Near-Term Features

### 13. Missing firmware test scenarios
- BULK_READ path (0x22 / 0x23) — not tested in any phase
- Gateway rule capacity limit (fill `MAX_ROUTING_RULES`, add one more)
- Byte mapping edge cases (shift-right, offset wrapping, out-of-bounds byte index)
- Config protocol error sequences (BULK_DATA before BULK_START, duplicate BULK_START)
- CAN sustained error rate detection (can2040 has no bus-off/TEC — PIO auto-recovers; add error-rate-over-time threshold reporting via diagnostics)
- NVM corruption recovery (both slots invalid, metadata sector corruption)
- `apply_config()` during active gateway traffic (regression test for race fixes)
- All 4 LIN channels active simultaneously with concurrent schedules

### 14. Integration test (firmware + config tool)
Automated test using `ConfigProtocol` class over a real CAN adapter. Exercises: connect, read all params, write rules, bulk transfer, read back, verify round-trip. Does not require CANoe.

### 15. Firmware code cleanup
- Remove dead variable `s_can_task_handle` (`can_manager.c`)
- Remove dead variable `s_can_tx_queue` (`config_handler.c`)
- Consolidate `can_bus_id_t` and `bus_id_t` enums
- Add `s_bulk_buffer` size comment (shared between routing rules and LIN schedules)
- Extract magic numbers in crash report frame construction to named constants
- Populate remaining `crash_data_t` fields in HardFault handler (CFSR, HFSR, PSP, MSP)

### 16. Config tool code cleanup
- Consolidate duplicate converter classes into `Helpers/Converters.cs`
- Remove dead properties (`RoutingRule.SrcBusName`, `DstBusName`, etc.)
- Move model classes out of `ProfilesViewModel.cs` into `Models/`
- Fix typo `ToByteMapppings` (three p's) in `BitMapping.cs`
- Remove misleading `[JsonIgnore]` on `RoutingRule.ProfileTag`

### 17. HMAC firmware signing
The bootloader supports `fw_hmac` at header offset 0x14 (32 bytes, HMAC-SHA256). Current firmware fills with 0xFF (unsigned).
- Add `fw_hmac[32]` field to `app_header_t`
- Update `patch_header.py` to accept `--key <keyfile>` and compute HMAC-SHA256
- Add signing step to build process

### 18. Extended CAN ID support (29-bit)
The bootloader supports `can_id_mode` config. Firmware's `check_bootloader_cmd()` filters standard 11-bit only.
- Read bootloader's `can_id_mode` at startup
- Adjust `check_bootloader_cmd()` to match extended frames when configured
- Support extended IDs in routing rules and config protocol

### 19. CAN "bridge all" mode
One-button mode that mirrors all CAN1 traffic to CAN2 and vice versa without individual routing rules. Useful for initial setup, debugging, and simple passthrough.

### 20. Config tool UI improvements
- **Logging:** File-based logging (Serilog) for adapter errors, protocol traces, CRC mismatches
- **Bulk transfer progress:** Progress bar for large transfers (2KB+ routing rules)
- **Busy state:** Disable buttons during async ops, add spinner, prevent double-clicks
- **Per-section read/write:** Individual section transfers instead of Read All / Write All only
- **Reboot button:** `ConfigProtocol.RebootAsync()` exists but no UI button
- **Frame monitor auto-scroll:** Add auto-scroll toggle to diagnostics frame log
- **Undo/redo:** Allow undoing config changes before writing to device

### 21. Hardware setup guide
Wiring, CAN termination, LIN connections, power supply requirements.

---

## P4 — Future Features

### 22. CAN1 runtime bitrate change
CAN1 bitrate only takes effect at boot. Requires stop/restart sequence, TX drain, and config tool reconnect at new bitrate. Risk: wrong bitrate makes device unreachable until reboot.

### 23. Data logging to secondary flash
Secondary flash (16 MB) has ample space beyond 12 KB NVM. Ring-buffer logger for timestamped CAN/LIN frames with configurable filters, start/stop via config protocol, and bulk readback.

### 24. Advanced gateway features
- **Conditional routing** — route based on data content (byte value thresholds)
- **Frame rate limiting** — per-rule forwarding rate limits to prevent bus flooding
- **J1939 / ISO-TP support** — multi-frame protocol for larger payloads
- **Per-rule statistics** — match count, last match timestamp, error count per routing rule

### 25. Multi-device management
- **Device serial number** — expose RP2350 unique chip ID via config protocol
- **Full device profiles** — save/restore complete device configurations (all CAN, LIN, routing, diag settings) as named config-tool profiles

### 26. Security hardening
- **Config protocol authentication** — challenge-response handshake before write operations
- **NVM config encryption** — encrypt sensitive data to prevent extraction via flash dumps
