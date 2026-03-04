#!/usr/bin/env python3
"""
Phase 4.5 Host-Side Tests: Debug Infrastructure
=================================================

Two-phase test: first boot validates heartbeat and on-target self-tests,
second boot (after CAN-triggered crash) validates crash reporting.

Requires:
  - Phase 4.5 test firmware flashed (built with -DTEST_PHASE4_5)
  - PCAN adapter connected to CAN1

Test flow:
  Phase A — First boot (auto-tests + heartbeat validation)
    T4.5.1   Crash data clear on cold boot
    T4.5.2   CAN1 active (heartbeat source)
    T4.5.3   CAN stats readable (rx_count > 0)
    T4.5.4   Heap readable
    T4.5.5   MCU temperature in sane range (10-70 C)
    T4.5.6   System state = OK
    T4.5.7   Reset reason = POWER_ON
    T4.5.8   Heap free > 1 KB
    T4.5.9   Stack watermark > 0
    T4.5.10  Watchdog feeding (alive for 3+ seconds)
    (host-side heartbeat validation)
    T4.5.H1  0x7F0 heartbeat frame received
    T4.5.H2  0x7F1 heartbeat frame received
    T4.5.H3  0x7F2 heartbeat frame received
    T4.5.H4  MCU temp in 0x7F0 byte 6 is sane (10-70 C)
    T4.5.H5  System state in 0x7F0 byte 4 is OK (0x01)
    T4.5.H6  Heap free in 0x7F2 byte 6 > 0
    T4.5.H7  Stack watermark in 0x7F2 byte 7 > 0
    T4.5.H8  Watchdog sustained (heartbeats for 10+ seconds)

  Phase B — Trigger configASSERT crash, wait for reboot
    T4.5.11  Crash report frame 0x7F3 appears
    T4.5.12  Fault type is ASSERT_FAIL (4)
    T4.5.13  Reset reason = CRASH_REBOOT (0x02)
    T4.5.14  Crash PC is non-zero

Usage:
  python tests/phase4_5/test_diag_host.py [--channel PCAN_USBBUS1]
"""

import sys
import os
import time
import argparse
import struct

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'common'))
from pcan_helper import PcanBus, TestResult

# CAN IDs
DIAG_STATUS_ID  = 0x7F0
DIAG_STATS_ID   = 0x7F1
DIAG_LIN_ID     = 0x7F2
DIAG_CRASH_ID   = 0x7F3
TEST_RESULT_ID  = 0x7FA
TEST_SUMMARY_ID = 0x7FB
TEST_CMD_ID     = 0x7F0  # Overloaded — host sends commands on this ID

# Test commands
CMD_TRIGGER_ASSERT    = 0x10
CMD_TRIGGER_STACK_OVF = 0x11
CMD_QUERY_CRASH_DATA  = 0x12

# Fault types
FAULT_NONE           = 0
FAULT_HARDFAULT      = 1
FAULT_STACK_OVERFLOW = 2
FAULT_MALLOC_FAIL    = 3
FAULT_ASSERT_FAIL    = 4
FAULT_WDT_TIMEOUT    = 5

FAULT_NAMES = {
    0: "NONE", 1: "HARDFAULT", 2: "STACK_OVERFLOW",
    3: "MALLOC_FAIL", 4: "ASSERT_FAIL", 5: "WATCHDOG_TIMEOUT"
}

RESULT_NAMES = {0x00: "PASS", 0x01: "FAIL", 0x02: "SKIP"}

# On-target test descriptions
TEST_DESCRIPTIONS = {
    1:  "Crash data state on boot",
    2:  "CAN1 active (heartbeat source)",
    3:  "CAN stats readable (no errors)",
    4:  "Heap readable",
    5:  "MCU temperature in sane range",
    6:  "System state = OK",
    7:  "Reset reason",
    8:  "Heap free > 1 KB",
    9:  "Stack watermark > 0",
    10: "Watchdog feeding (alive)",
    11: "Crash data valid post-crash",
    12: "Fault type matches",
    13: "Reset reason = CRASH_REBOOT",
    14: "Crash PC non-zero",
}


def collect_on_target_results(bus, results, timeout=20.0):
    """Collect test results from on-target firmware (0x7FA frames).
    Also captures any 0x7F3 crash report frames that arrive during collection.
    Returns (summary_received, list_of_crash_frames)."""
    deadline = time.time() + timeout
    summary_received = False
    on_target_total = 0
    on_target_passed = 0
    crash_frames = []

    while time.time() < deadline and not summary_received:
        msg = bus.recv_frame(timeout=1.0)
        if not msg:
            continue

        if msg.arbitration_id == TEST_RESULT_ID and len(msg.data) >= 2:
            test_id = msg.data[0]
            result_code = msg.data[1]
            extra = list(msg.data[2:msg.dlc]) if msg.dlc > 2 else []

            if test_id == 0xFF:
                # Crash query response — not a test result
                continue

            desc = TEST_DESCRIPTIONS.get(test_id, f"Unknown test {test_id}")
            detail = ' '.join(f"0x{b:02X}" for b in extra) if extra else ""

            results.check(f"T4.5.{test_id}", desc,
                          result_code == 0x00, detail)
            on_target_total += 1
            if result_code == 0x00:
                on_target_passed += 1

        elif msg.arbitration_id == TEST_SUMMARY_ID and len(msg.data) >= 4:
            total = msg.data[0]
            passed = msg.data[1]
            failed = msg.data[2]
            sentinel = msg.data[3]

            if sentinel == 0xAA:
                print(f"\n  Target summary: {passed}/{total} passed, {failed} failed")
                summary_received = True

        elif msg.arbitration_id == DIAG_CRASH_ID:
            crash_frames.append(msg)

    if not summary_received:
        print("\n  WARNING: No target summary received (timeout)")

    return summary_received, crash_frames


def collect_heartbeat_frames(bus, duration=12.0):
    """Collect heartbeat frames for the given duration.
    Returns dicts of captured frames keyed by CAN ID."""
    frames = {DIAG_STATUS_ID: [], DIAG_STATS_ID: [], DIAG_LIN_ID: [], DIAG_CRASH_ID: []}

    deadline = time.time() + duration
    while time.time() < deadline:
        msg = bus.recv_frame(timeout=0.5)
        if msg and msg.arbitration_id in frames:
            frames[msg.arbitration_id].append(msg)

    return frames


def validate_heartbeat(results, frames):
    """Validate heartbeat frame content from collected frames."""

    # T4.5.H1: 0x7F0 received
    got_7f0 = len(frames[DIAG_STATUS_ID]) > 0
    results.check("T4.5.H1", "0x7F0 heartbeat received",
                  got_7f0, f"count={len(frames[DIAG_STATUS_ID])}")

    # T4.5.H2: 0x7F1 received
    got_7f1 = len(frames[DIAG_STATS_ID]) > 0
    results.check("T4.5.H2", "0x7F1 heartbeat received",
                  got_7f1, f"count={len(frames[DIAG_STATS_ID])}")

    # T4.5.H3: 0x7F2 received
    got_7f2 = len(frames[DIAG_LIN_ID]) > 0
    results.check("T4.5.H3", "0x7F2 heartbeat received",
                  got_7f2, f"count={len(frames[DIAG_LIN_ID])}")

    if got_7f0:
        last_7f0 = frames[DIAG_STATUS_ID][-1]
        d = list(last_7f0.data)

        # T4.5.H4: MCU temp sane
        temp = d[6] if d[6] < 128 else d[6] - 256  # signed int8
        results.check("T4.5.H4", f"MCU temp sane ({temp} C)",
                      10 <= temp <= 70, f"raw=0x{d[6]:02X}")

        # T4.5.H5: System state = OK
        sys_state = d[4]
        results.check("T4.5.H5", "System state = OK (0x01)",
                      sys_state == 0x01, f"state=0x{sys_state:02X}")

    if got_7f2:
        last_7f2 = frames[DIAG_LIN_ID][-1]
        d = list(last_7f2.data)

        # T4.5.H6: Heap free > 0
        heap_kb = d[6]
        results.check("T4.5.H6", f"Heap free > 0 ({heap_kb} KB)",
                      heap_kb > 0, f"heap_free={heap_kb} KB")

        # T4.5.H7: Stack watermark > 0
        stack_wm = d[7]
        results.check("T4.5.H7", f"Stack watermark > 0 ({stack_wm} words)",
                      stack_wm > 0, f"min_watermark={stack_wm}")

    # T4.5.H8: Watchdog sustained — need multiple heartbeats
    count_7f0 = len(frames[DIAG_STATUS_ID])
    results.check("T4.5.H8", f"Watchdog sustained ({count_7f0} heartbeats)",
                  count_7f0 >= 8,
                  f"expected >=8 in ~12s, got {count_7f0}")


def run_tests(channel: str, bitrate: int, timeout: float):
    print("=" * 60)
    print("  Phase 4.5: Debug Infrastructure Tests")
    print(f"  Channel: {channel}, Bitrate: {bitrate}")
    print("=" * 60)

    results = TestResult()

    with PcanBus(channel=channel, bitrate=bitrate) as bus:
        # ================================================================
        # PHASE A: First boot — auto-tests + heartbeat validation
        # ================================================================
        print("\n" + "=" * 60)
        print("  Phase A: First Boot Validation")
        print("  (Reset board now if not freshly powered)")
        print("=" * 60)

        # Wait briefly for board to boot
        time.sleep(1.0)
        bus.flush_rx(0.5)

        # 1) Collect on-target test results
        print("\n--- Collecting on-target test results ---")
        got_summary, _ = collect_on_target_results(bus, results, timeout=20.0)

        # 2) Collect heartbeat frames for ~12 seconds
        print("\n--- Collecting heartbeat frames (12 seconds) ---")
        hb_frames = collect_heartbeat_frames(bus, duration=12.0)

        # 3) Validate heartbeat content
        print("\n--- Validating heartbeat content ---")
        validate_heartbeat(results, hb_frames)

        # 4) Verify no crash report on clean boot
        crash_on_clean = len(hb_frames[DIAG_CRASH_ID]) > 0
        # Note: crash report may or may not appear depending on previous state.
        # We don't fail on this — it's informational.
        if crash_on_clean:
            print(f"  [INFO] Crash report 0x7F3 seen on boot (previous crash data)")

        # ================================================================
        # PHASE B: Trigger crash and validate crash reporting
        # ================================================================
        print("\n" + "=" * 60)
        print("  Phase B: Crash Trigger + Recovery")
        print("=" * 60)

        print("\n--- Triggering configASSERT(0) via CAN command ---")
        bus.flush_rx(0.2)

        # Send assert trigger command
        bus.send_frame(TEST_CMD_ID, [CMD_TRIGGER_ASSERT, 0, 0, 0, 0, 0, 0, 0])

        # Wait for board to crash and reboot (watchdog_reboot is near-instant,
        # but CAN re-init takes ~500ms + test startup ~500ms)
        print("  Waiting for reboot...")
        time.sleep(4.0)
        bus.flush_rx(0.5)

        # Collect post-crash on-target results (also captures 0x7F3 crash report)
        print("\n--- Collecting post-crash test results ---")
        got_summary_b, crash_frames_from_results = collect_on_target_results(bus, results, timeout=20.0)

        # Also collect heartbeat frames to verify crash report
        print("\n--- Collecting post-crash heartbeat frames (5 seconds) ---")
        hb_frames_b = collect_heartbeat_frames(bus, duration=5.0)

        # Check for crash report frame (may arrive during result collection or heartbeat window)
        crash_frames = crash_frames_from_results + hb_frames_b[DIAG_CRASH_ID]
        if crash_frames:
            cf = crash_frames[0]
            d = list(cf.data)
            fault_type = d[0]
            pc = (d[1] << 24) | (d[2] << 16) | (d[3] << 8) | d[4]
            crash_uptime = (d[5] << 8) | d[6]
            task_char = chr(d[7]) if 32 <= d[7] < 127 else '?'

            print(f"\n  Crash report: fault={FAULT_NAMES.get(fault_type, '?')}({fault_type}), "
                  f"PC=0x{pc:08X}, uptime={crash_uptime}s, task='{task_char}'")

            # These are supplementary host-side checks
            results.check("T4.5.H9", "Crash report 0x7F3 received",
                          True, f"fault={FAULT_NAMES.get(fault_type, '?')}")
            results.check("T4.5.H10", "Crash fault type = ASSERT_FAIL",
                          fault_type == FAULT_ASSERT_FAIL,
                          f"got {FAULT_NAMES.get(fault_type, '?')}({fault_type})")
            results.check("T4.5.H11", "Crash PC non-zero",
                          pc != 0, f"PC=0x{pc:08X}")
        else:
            results.check("T4.5.H9", "Crash report 0x7F3 received",
                          False, "no crash report frame seen")
            results.check("T4.5.H10", "Crash fault type = ASSERT_FAIL",
                          False, "no crash frame")
            results.check("T4.5.H11", "Crash PC non-zero",
                          False, "no crash frame")

        # Check post-crash reset reason in heartbeat
        if hb_frames_b[DIAG_STATUS_ID]:
            last_7f0 = hb_frames_b[DIAG_STATUS_ID][-1]
            reset_reason = list(last_7f0.data)[7]
            reason_names = {0: "POWER_ON", 1: "WATCHDOG_TIMEOUT",
                            2: "CRASH_REBOOT", 3: "UNKNOWN"}
            results.check("T4.5.H12", "Reset reason = CRASH_REBOOT in heartbeat",
                          reset_reason == 2,
                          f"reason={reason_names.get(reset_reason, '?')}({reset_reason})")
        else:
            results.check("T4.5.H12", "Reset reason in heartbeat",
                          False, "no 0x7F0 frame after crash reboot")

    return results.summary()


def main():
    parser = argparse.ArgumentParser(description="Phase 4.5 Debug Infrastructure Tests")
    parser.add_argument('--channel', default='PCAN_USBBUS1',
                        help='PCAN channel (default: PCAN_USBBUS1)')
    parser.add_argument('--bitrate', type=int, default=500000,
                        help='CAN bitrate (default: 500000)')
    parser.add_argument('--timeout', type=float, default=30.0,
                        help='Per-phase timeout in seconds (default: 30)')
    args = parser.parse_args()

    success = run_tests(args.channel, args.bitrate, args.timeout)
    sys.exit(0 if success else 1)


if __name__ == '__main__':
    main()
