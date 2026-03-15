# CanLinBoard Gateway Integration Test Protocol

## 1. Purpose

Verify end-to-end gateway routing between CAN and LIN buses using a Vector interface (3x CAN + 1x LIN) running CANoe with the automated CAPL test script.

## 2. Equipment Required

| Item | Details |
|------|---------|
| CanLinBoard | Firmware flashed, powered |
| Vector interface | 3x CAN channels + 1x LIN channel (e.g. VN1640, VN8900) |
| CANoe | Version 11 or later |
| CAN cables | 2x DB9 or custom harness |
| LIN cable | 1x (optional, for T8/T9) |
| 120 ohm terminators | If not using board-side termination |
| PC | Windows, with CanLinConfig tool built |

## 3. Physical Wiring

```
  Vector CAN ch1 --------[ CAN_H/CAN_L ]-------- Board CAN1
  Vector CAN ch2 --------[ CAN_H/CAN_L ]-------- Board CAN2
  Vector LIN ch1 --------[ LIN / GND   ]-------- Board LIN1 (optional)
  Vector CAN ch3 -------- (unused, available for sniffer/monitor)
```

- CAN1 and CAN2 buses must each have exactly one 120 ohm termination. The board config enables termination on both buses, so do **not** add external terminators unless board termination is disabled.
- LIN requires a common ground between Vector interface and board. The board LIN1 master provides the pull-up.

## 4. Software Preparation

### 4.1 Load Board Configuration

1. Build and launch the CanLinConfig tool:
   ```
   cd software
   dotnet run --project CanLinConfig
   ```
2. Connect to the board on CAN1 (500 kbps).
3. **File > Import** and select `tests/gateway_test_config.json`.
4. Review the loaded configuration:
   - CAN1: 500 kbps, termination ON, enabled
   - CAN2: 500 kbps, termination ON, enabled
   - LIN1: master mode, 19200 baud, 2-entry schedule (ID 16 publish, ID 17 subscribe)
   - LIN2-4: disabled
   - 6 routing rules (see Section 6)
   - Diagnostics: enabled, 1 s interval on CAN1
5. Click **Write All** to send the config to the board.
6. Click **Save to NVM** so the config persists across reboots.
7. Power-cycle or reboot the board to apply the full config cleanly.

### 4.2 CANoe Project Setup

1. Create a new CANoe configuration.
2. Add two CAN networks:
   - **CAN1** — mapped to Vector HW channel 1, 500 kbps
   - **CAN2** — mapped to Vector HW channel 2, 500 kbps
3. (Optional) Add one LIN network:
   - **LIN1** — mapped to Vector LIN HW channel, 19200 baud, **slave mode**
4. Add a simulation/test CAPL node and attach it to **all** networks (CAN1, CAN2, and optionally LIN1).
5. Assign `tests/gateway_test.can` to the CAPL node.
6. (Optional) Load `docs/CanLinBoard.dbc` as the database for CAN1 to get signal decoding in trace windows.
7. If channel numbers differ from the defaults (`CH_BOARD_CAN1=1`, `CH_BOARD_CAN2=2`), edit the constants in the CAPL `variables` block.
8. To enable LIN tests (T8, T9), set `gLinTestsEnabled = 1` in the CAPL `variables` block.

## 5. Board Configuration Summary

### 5.1 CAN Buses

| Bus | Bitrate | Termination | Enabled |
|-----|---------|-------------|---------|
| CAN1 | 500 kbps | ON | Always |
| CAN2 | 500 kbps | ON | Yes |

### 5.2 LIN Configuration

| Channel | Mode | Baud Rate | Schedule |
|---------|------|-----------|----------|
| LIN1 | Master | 19200 | ID 16 (publish, DLC=4, 20 ms) + ID 17 (subscribe, DLC=4, 20 ms) |
| LIN2-4 | Disabled | — | — |

### 5.3 Routing Rules

| # | Source Bus | Source ID | Mask | Dest Bus | Dest ID | DLC | Byte Mappings | Tag |
|---|-----------|----------|------|----------|---------|-----|---------------|-----|
| 1 | CAN2 | 0x100 | 0x7FF | CAN1 | 0x200 | auto | none (full passthrough) | test-id-translation |
| 2 | CAN1 | 0x300 | 0x7FF | CAN2 | passthrough | auto | none (full passthrough) | test-passthrough |
| 3 | CAN1 | 0x400 | 0x7FF | CAN2 | 0x401 | auto | src[0]->dst[1], src[1]->dst[0] | test-byte-swap |
| 4 | CAN2 | 0x500 | 0x7F0 | CAN1 | passthrough | auto | none (full passthrough) | test-mask-match |
| 5 | CAN1 | 0x280 | 0x7FF | LIN1 | ID 16 | 4 | src[0..3]->dst[0..3] | test-can-to-lin |
| 6 | LIN1 | ID 17 | 0x3F | CAN1 | 0x281 | 4 | src[0..3]->dst[0..3] | test-lin-to-can |

### 5.4 Diagnostics

| Parameter | Value |
|-----------|-------|
| Heartbeat CAN ID | 0x7F0 |
| Interval | 1000 ms |
| Bus | CAN1 |
| CAN watchdog | 5000 ms |
| LIN watchdog | 5000 ms |

## 6. Test Procedure

Start measurement in CANoe. The CAPL script waits 3 seconds for the board to boot, then runs all tests automatically in sequence. Results appear in the CANoe Write window.

### T1 — Config Protocol CONNECT

| Field | Value |
|-------|-------|
| Purpose | Verify the board responds to the config protocol handshake |
| Stimulus | Send `0x01` (CONNECT) on CAN1 ID `0x600`, DLC=1 |
| Expected | Response on CAN1 ID `0x601`: byte[0]=0x01 (echo), byte[1]=0x00 (OK), bytes[2-4]=FW version |
| Timeout | 1000 ms |
| Pass criteria | Status byte is 0x00, firmware version is reported |

### T2 — CAN2 to CAN1 ID Translation

| Field | Value |
|-------|-------|
| Purpose | Verify routing with CAN ID remapping |
| Rule | #1: CAN2 0x100 -> CAN1 0x200 |
| Stimulus | Send on CAN ch2, ID=0x100, DLC=8, data=`11 22 33 44 55 66 77 88` |
| Expected | Receive on CAN ch1, ID=0x200, DLC=8, data=`11 22 33 44 55 66 77 88` |
| Timeout | 500 ms |
| Pass criteria | ID translated to 0x200, all 8 data bytes match exactly |

### T3 — CAN1 to CAN2 Passthrough

| Field | Value |
|-------|-------|
| Purpose | Verify pass-through routing (same ID on destination) |
| Rule | #2: CAN1 0x300 -> CAN2 0x300 (passthrough) |
| Stimulus | Send on CAN ch1, ID=0x300, DLC=4, data=`AA BB CC DD` |
| Expected | Receive on CAN ch2, ID=0x300, DLC=4, data=`AA BB CC DD` |
| Timeout | 500 ms |
| Pass criteria | Same ID appears on CAN2, first 4 data bytes match |

### T4 — CAN1 to CAN2 Byte Swap

| Field | Value |
|-------|-------|
| Purpose | Verify byte-level data mapping (swap bytes 0 and 1) plus ID change |
| Rule | #3: CAN1 0x400 -> CAN2 0x401, mapping src[0]->dst[1] and src[1]->dst[0] |
| Stimulus | Send on CAN ch1, ID=0x400, DLC=8, data=`AA 55 33 44 55 66 77 88` |
| Expected | Receive on CAN ch2, ID=0x401, byte[0]=0x55, byte[1]=0xAA |
| Timeout | 500 ms |
| Pass criteria | Destination ID is 0x401 and bytes 0/1 are swapped |

### T5 — CAN2 to CAN1 Mask-Based Routing

| Field | Value |
|-------|-------|
| Purpose | Verify mask matching routes a range of IDs (0x500-0x50F) |
| Rule | #4: CAN2 0x500, mask 0x7F0 -> CAN1 passthrough |
| Stimulus | Send on CAN ch2, ID=0x505, DLC=3, data=`DE AD 01` |
| Expected | Receive on CAN ch1, ID=0x505, DLC=3, data=`DE AD 01` |
| Timeout | 500 ms |
| Pass criteria | Frame 0x505 routed with original ID preserved, data intact |

### T6 — Burst Routing (Stress)

| Field | Value |
|-------|-------|
| Purpose | Verify no frame loss under rapid-fire conditions |
| Rule | #1: CAN2 0x100 -> CAN1 0x200 |
| Stimulus | Send 10 frames back-to-back on CAN ch2, ID=0x100, DLC=1, data[0]=0..9 |
| Expected | Receive 10 frames on CAN ch1, ID=0x200 |
| Timeout | 2000 ms |
| Pass criteria | All 10 frames received on CAN1 (count >= 10) |

### T7 — Negative Test (No Route)

| Field | Value |
|-------|-------|
| Purpose | Verify that frames with no matching rule are NOT forwarded |
| Stimulus | Send on CAN ch2, ID=0x1FF, DLC=2, data=`BA AD` |
| Expected | ID 0x1FF must NOT appear on CAN ch1 within 400 ms |
| Timeout | 500 ms |
| Pass criteria | No frame with ID 0x1FF received on CAN1 |

### T8 — CAN to LIN Bridge (Optional)

| Field | Value |
|-------|-------|
| Purpose | Verify CAN-to-LIN cross-protocol routing |
| Rule | #5: CAN1 0x280 -> LIN1 ID 16, bytes 0-3 mapped |
| Prerequisite | LIN wired, `gLinTestsEnabled=1`, board LIN1 master with schedule |
| Stimulus | Send on CAN ch1, ID=0x280, DLC=4, data=`CA FE BA BE` |
| Expected | Board transmits LIN frame ID 16 (0x10) with data `CA FE BA BE` on next schedule cycle |
| Timeout | 500 ms |
| Pass criteria | LIN frame captured by Vector slave matches transmitted CAN data |
| Skipped if | `gLinTestsEnabled=0` |

### T9 — LIN to CAN Bridge (Optional)

| Field | Value |
|-------|-------|
| Purpose | Verify LIN-to-CAN cross-protocol routing |
| Rule | #6: LIN1 ID 17 -> CAN1 0x281, bytes 0-3 mapped |
| Prerequisite | LIN wired, `gLinTestsEnabled=1`, Vector LIN configured as slave |
| Stimulus | Board (master) sends LIN header for ID 17 (0x11); Vector slave responds with `DE AD BE EF` |
| Expected | Receive on CAN ch1, ID=0x281, DLC=4, data=`DE AD BE EF` |
| Timeout | 500 ms |
| Pass criteria | CAN frame data matches the LIN slave response |
| Skipped if | `gLinTestsEnabled=0` |

### T10 — Diagnostics Heartbeat Monitoring

| Field | Value |
|-------|-------|
| Purpose | Verify the board sends periodic diagnostic frames on CAN1 |
| Monitored IDs | 0x7F0 (Status), 0x7F1 (CAN Stats), 0x7F4 (System Health) |
| Window | 3000 ms |
| Timeout | 4000 ms |
| Pass criteria | At least 2 instances of each diagnostic frame received within the 3 s window |
| Additional output | SysState, MCU temperature, bus mask, heap free, min stack watermark, watchdog mask |

## 7. Expected Output

Successful run with LIN disabled (Write window):

```
============================================================
 CanLinBoard Gateway Integration Test
============================================================
  CAN ch1  <-->  Board CAN1 (config + gateway peer)
  CAN ch2  <-->  Board CAN2 (gateway peer)
  LIN tests DISABLED (set gLinTestsEnabled=1 to enable)
------------------------------------------------------------
Waiting 3 s for board boot...

[T1] Config Protocol - CONNECT
  PASS - Connected - FW v1.0.0, cfg_size=3161, rules=6
[T2] CAN2->CAN1 ID translation (0x100 -> 0x200)
  PASS - ID 0x100->0x200, all 8 data bytes match
[T3] CAN1->CAN2 passthrough (0x300 -> 0x300)
  PASS - Passthrough 0x300, data intact (DLC=4)
[T4] CAN1->CAN2 byte swap (0x400 -> 0x401, bytes 0<->1)
  PASS - Byte swap confirmed: dst[0]=0x55, dst[1]=0xAA
[T5] CAN2->CAN1 mask routing (0x505 matches mask 0x7F0)
  PASS - Mask match 0x505 routed with passthrough ID, data OK
[T6] Burst routing: 10 frames CAN2->CAN1 (0x100->0x200)
  PASS - All 10 burst frames routed successfully
[T7] Negative test: 0x1FF on CAN2 should NOT route to CAN1
  PASS - No routing for unmatched ID 0x1FF (correct)
[T8] CAN1->LIN1 bridge (0x280 -> LIN ID 0x10)
  SKIP - LIN tests disabled
[T9] LIN1->CAN1 bridge (LIN ID 0x11 -> 0x281)
  SKIP - LIN tests disabled
[T10] Diagnostics heartbeat monitoring (3 s window)
    Diag: SysState=1, MCU_Temp=32 C, BusMask=0x07
    Health: HeapFree=180 KB, MinStack=64 words, WdtMask=0x00
  PASS - Heartbeat in 3s: Status=3, CANStats=3, Health=3 (expect >=2 each)

============================================================
 TEST SUMMARY
============================================================
  Total:   10
  Passed:  8
  Failed:  0
  Skipped: 2
============================================================
  >>> ALL TESTS PASSED <<<
```

## 8. Troubleshooting

| Symptom | Possible Cause | Action |
|---------|---------------|--------|
| T1 times out | Board not powered, wrong CAN channel, bitrate mismatch | Verify wiring, check CANoe channel mapping, confirm 500 kbps |
| T1 returns status != 0x00 | Board busy or in error state | Power-cycle the board and retry |
| T2-T5 time out | Config not loaded or not saved to NVM | Re-import `gateway_test_config.json`, Write All, Save to NVM, reboot |
| T4 data wrong | Byte mapping misconfigured | Verify rule 3 mappings in config: src[0]->dst[1] and src[1]->dst[0] |
| T6 incomplete count | TX buffer overflow at 500 kbps under burst | Increase timeout; check CAN error counters in trace |
| T7 fails (frame routed) | Unexpected routing rule matches 0x1FF | Review all rules — ensure no mask accidentally matches 0x1FF |
| T8/T9 time out | LIN not wired, wrong baud, slave not configured | Verify LIN cable, check CANoe LIN HW config is slave at 19200 baud |
| T10 insufficient heartbeats | Diagnostics disabled in config, wrong CAN ID | Verify diag config: enabled=true, can_id=0x7F0, interval=1000 ms |
| No TX on CAN ch2 at all | CAN2 not enabled on board | Check config: CAN2 enabled=true; verify with `READ_PARAM` section=0, param=2, sub=1 |

## 9. Files

| File | Description |
|------|-------------|
| `tests/gateway_test.can` | CAPL test script (10 automated tests) |
| `tests/gateway_test_config.json` | Board configuration (import via CanLinConfig tool) |
| `docs/CanLinBoard.dbc` | CAN database for signal decoding in CANoe trace |
