#!/usr/bin/env python3
"""
Phase 3 Host-Side Test Collector: LIN Subsystem
================================================

Listens on PCAN for test results from the Phase 3 on-target test firmware.

The target runs auto-tests on boot and reports results on CAN1 ID 0x7FA.

Tests verified:
  T3.1   PLL lock with 8 MHz clock
  T3.2   PLL lock failure (skipped in auto)
  T3.3   Channel init to Normal/Idle mode
  T3.4   Master header TX (no crash, state transition)
  T3.5   Master publish (8-byte frame TX)
  T3.6   Slave response RX (requires external slave — manual)
  T3.7   Multi-channel init (all 4 channels)
  T3.8   Baud rate register accuracy
  T3.9   Schedule table (manual — via command interface)
  T3.10  Schedule wrap (manual)
  T3.11  Timeout error (no slave connected)
  T3.12  Checksum error (requires corrupted response — manual)
  T3.13  Channel stop/restart
  T3.14  Mode switch (master ↔ slave)

Usage:
  1. Flash Phase 3 test firmware (built with -DTEST_PHASE3)
  2. Connect PCAN to CAN1
  3. Run: python tests/phase3/test_lin_host.py [--channel PCAN_USBBUS1]
  4. Power/reset the board
  5. Auto-tests run immediately; results appear within ~10 seconds
"""

import sys
import os
import time
import argparse

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'common'))
from pcan_helper import PcanBus, TestResult

TEST_RESULT_ID = 0x7FA
TEST_SUMMARY_ID = 0x7FB
TEST_CMD_ID = 0x7F0

RESULT_NAMES = {0x00: "PASS", 0x01: "FAIL", 0x02: "SKIP"}

SJA_ERROR_NAMES = {
    0: "SJA_OK",
    1: "SJA_ERR_SPI",
    2: "SJA_ERR_PLL_LOCK",
    3: "SJA_ERR_PLL_FREQ",
    4: "SJA_ERR_TIMEOUT",
    5: "SJA_ERR_INVALID_PARAM",
    6: "SJA_ERR_NOT_INIT",
    7: "SJA_ERR_BUSY",
}

LINS_STATE_NAMES = {
    0: "Sleep", 1: "Init", 2: "Idle", 3: "Break TX",
    4: "Break Delim", 5: "Sync TX", 6: "ID TX",
    7: "Header Done", 8: "Response", 9: "Checksum",
}

TEST_DESCRIPTIONS = {
    1:  "PLL lock with 8 MHz input",
    2:  "PLL lock failure (clock removed)",
    3:  "LIN1 channel init → Normal/Idle",
    4:  "Master header TX on LIN1",
    5:  "Master publish (8-byte frame) on LIN1",
    6:  "Slave response RX (external slave needed)",
    7:  "All 4 channels init independently",
    8:  "Baud rate register accuracy (9600 baud)",
    9:  "Schedule table execution",
    10: "Schedule table wrap",
    11: "Timeout error (no slave)",
    12: "Checksum error detection",
    13: "Channel stop/restart cycle",
    14: "Mode switch (master ↔ slave)",
}


def decode_test_extra(test_id, extra):
    """Decode test-specific extra bytes into human-readable string."""
    if not extra:
        return ""

    if test_id == 1:
        err = SJA_ERROR_NAMES.get(extra[0], f"0x{extra[0]:02X}") if len(extra) > 0 else "?"
        locked = extra[1] if len(extra) > 1 else 0
        return f"err={err}, pll_locked={locked}"

    if test_id == 3:
        err = SJA_ERROR_NAMES.get(extra[0], f"0x{extra[0]:02X}") if len(extra) > 0 else "?"
        lstate = extra[1] if len(extra) > 1 else 0
        lins = extra[2] if len(extra) > 2 else 0
        state_name = LINS_STATE_NAMES.get(lins, f"0x{lins:X}")
        return f"err={err}, LSTATE=0x{lstate:02X}, LINS={state_name}"

    if test_id in (4, 5):
        err = SJA_ERROR_NAMES.get(extra[0], f"0x{extra[0]:02X}") if len(extra) > 0 else "?"
        ls = extra[1] if len(extra) > 1 else 0
        les = extra[2] if len(extra) > 2 else 0
        errors = []
        if les & 0x80: errors.append("SZF")
        if les & 0x40: errors.append("TOF")
        if les & 0x20: errors.append("BEF")
        if les & 0x10: errors.append("CEF")
        if les & 0x01: errors.append("FEF")
        return f"err={err}, LS=0x{ls:02X}, LES=0x{les:02X} [{','.join(errors) or 'none'}]"

    if test_id == 7:
        init_bits = extra[0] if len(extra) > 0 else 0
        state_bits = extra[1] if len(extra) > 1 else 0
        inits = [f"ch{i}={'ok' if init_bits & (1<<i) else 'FAIL'}" for i in range(4)]
        states = [f"ch{i}={'idle' if state_bits & (1<<i) else 'NOT IDLE'}" for i in range(4)]
        return f"init=[{','.join(inits)}] state=[{','.join(states)}]"

    if test_id == 8:
        if len(extra) >= 3:
            ibr = (extra[0] << 8) | extra[1]
            fbr = extra[2]
            return f"LBRM=0x{extra[0]:02X}, LBRL=0x{extra[1]:02X}, FBR={fbr}, IBR={ibr}"

    if test_id == 11:
        les = extra[0] if len(extra) > 0 else 0
        tof = extra[1] if len(extra) > 1 else 0
        return f"LES=0x{les:02X}, TOF_set={tof}"

    if test_id == 13:
        lstate_stop = extra[0] if len(extra) > 0 else 0
        lstate_restart = extra[1] if len(extra) > 1 else 0
        ok = extra[2] if len(extra) > 2 else 0
        stop_state = LINS_STATE_NAMES.get(lstate_stop & 0x0F, "?")
        restart_state = LINS_STATE_NAMES.get(lstate_restart & 0x0F, "?")
        return f"stopped={stop_state}, restarted={restart_state}"

    if test_id == 14:
        err = SJA_ERROR_NAMES.get(extra[0], "?") if len(extra) > 0 else "?"
        lstate = extra[1] if len(extra) > 1 else 0
        lins = extra[2] if len(extra) > 2 else 0
        return f"err={err}, LINS={LINS_STATE_NAMES.get(lins, '?')}"

    return ' '.join(f"0x{b:02X}" for b in extra)


def run_collection(channel: str, bitrate: int, timeout: float):
    print("=" * 60)
    print("  Phase 3: LIN Subsystem Tests — Collecting Results")
    print(f"  Channel: {channel}, Bitrate: {bitrate}")
    print("=" * 60)
    print("\n  Listening for test results... (reset the board now)\n")

    results = TestResult()

    with PcanBus(channel=channel, bitrate=bitrate) as bus:
        deadline = time.time() + timeout
        summary_received = False

        while time.time() < deadline and not summary_received:
            msg = bus.recv_frame(timeout=1.0)
            if not msg:
                continue

            if msg.arbitration_id == TEST_RESULT_ID and len(msg.data) >= 2:
                test_id = msg.data[0]
                result_code = msg.data[1]
                extra = list(msg.data[2:msg.dlc]) if msg.dlc > 2 else []

                result_str = RESULT_NAMES.get(result_code, f"0x{result_code:02X}")
                desc = TEST_DESCRIPTIONS.get(test_id, f"Unknown test {test_id}")
                detail = decode_test_extra(test_id, extra)

                if result_code == 0x02:  # SKIP
                    print(f"  [SKIP] T3.{test_id}: {desc} — {detail}")
                else:
                    results.check(f"T3.{test_id}", desc,
                                  result_code == 0x00, detail)

            elif msg.arbitration_id == TEST_SUMMARY_ID and len(msg.data) >= 4:
                total = msg.data[0]
                passed = msg.data[1]
                failed = msg.data[2]
                sentinel = msg.data[3]

                if sentinel == 0xAA:
                    print(f"\n  Target summary: {passed}/{total} passed, {failed} failed")
                    summary_received = True

        if not summary_received:
            print("\n  WARNING: No summary received (timeout)")
            print("  Possible causes:")
            print("    - Board not powered/reset")
            print("    - Wrong CAN channel/bitrate")
            print("    - Test firmware not flashed (need -DTEST_PHASE3)")
            print("    - SJA1124 not responding (check SPI wiring, clock)")

    print("\n  Manual verification required (oscilloscope):")
    print("    T3.4:  Break + sync + ID fields at 19200 baud on LIN1")
    print("    T3.5:  Complete 8-byte frame with checksum on LIN1")
    print("    T3.8:  Bit timing within +/-2% of 9600 baud")
    print("    T1.11: 8 MHz clock on GPIO 21 within +/-24 kHz")

    return results.summary()


def main():
    parser = argparse.ArgumentParser(description="Phase 3 LIN Test Collector")
    parser.add_argument('--channel', default='PCAN_USBBUS1',
                        help='PCAN channel (default: PCAN_USBBUS1)')
    parser.add_argument('--bitrate', type=int, default=500000,
                        help='CAN bitrate (default: 500000)')
    parser.add_argument('--timeout', type=float, default=20.0,
                        help='Collection timeout in seconds (default: 20)')
    args = parser.parse_args()

    success = run_collection(args.channel, args.bitrate, args.timeout)
    sys.exit(0 if success else 1)


if __name__ == '__main__':
    main()
