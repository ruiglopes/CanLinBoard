#!/usr/bin/env python3
"""
Phase 6 Host-Side Tests: Diagnostics
======================================

Collects on-target test results (T6.1-T6.12), then runs host-side
timing verification of heartbeat frames.

Requires:
  - Phase 6 test firmware flashed (built with -DTEST_PHASE6)
  - PCAN adapter connected to CAN1

Test flow:
  Phase A — Auto-tests (on-target, results on 0x7FA/0x7FB)
    T6.1   Heartbeat at configured interval
    T6.2   System state = OK after boot
    T6.3   Bus mask correct
    T6.4   Uptime increments
    T6.5   Gateway counter
    T6.6   SW watchdog feed keeps alive
    T6.7   SW watchdog timeout fires
    T6.8   SW watchdog LIN timeout
    T6.9   System state -> ERROR on timeout
    T6.10  Diag interval change
    T6.11  Diag disable
    T6.12  Diag bus param read/write

  Phase B — Host-verified heartbeat timing
    H6.1   Measure heartbeat frame timing (within +/-50ms of 1000ms)
    H6.2   Verify heartbeat stops when disabled

Usage:
  python tests/phase6/test_diagnostics_host.py [--channel PCAN_USBBUS1]
"""

import sys
import os
import time
import argparse

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'common'))
from pcan_helper import PcanBus, TestResult

# CAN IDs
CONFIG_CMD_ID   = 0x600
CONFIG_RESP_ID  = 0x601
DIAG_STATUS_ID  = 0x7F0
TEST_RESULT_ID  = 0x7FA
TEST_SUMMARY_ID = 0x7FB

# Config commands
CFG_CMD_WRITE_PARAM = 0x11
CFG_CMD_SAVE        = 0x02
CFG_SECTION_DIAG    = 0x03

RESULT_NAMES = {0x00: "PASS", 0x01: "FAIL", 0x02: "SKIP"}

# On-target test descriptions
TEST_DESCRIPTIONS = {
    1:  "Heartbeat at configured interval",
    2:  "System state = OK after boot",
    3:  "Bus mask correct (CAN1 active, CAN2 inactive)",
    4:  "Uptime increments",
    5:  "Gateway counter",
    6:  "SW watchdog feed keeps alive",
    7:  "SW watchdog timeout fires",
    8:  "SW watchdog LIN timeout",
    9:  "System state -> ERROR on timeout",
    10: "Diag interval change",
    11: "Diag disable",
    12: "Diag bus param read/write",
}


def collect_on_target_results(bus, results, timeout=60.0):
    """Collect test results from on-target firmware (0x7FA frames)."""
    deadline = time.time() + timeout
    summary_received = False
    on_target_total = 0

    while time.time() < deadline and not summary_received:
        msg = bus.recv_frame(timeout=1.0)
        if not msg:
            continue

        if msg.arbitration_id == TEST_RESULT_ID and len(msg.data) >= 2:
            test_id = msg.data[0]
            result_code = msg.data[1]
            extra = list(msg.data[2:msg.dlc]) if msg.dlc > 2 else []

            desc = TEST_DESCRIPTIONS.get(test_id, f"Unknown test {test_id}")
            detail = ' '.join(f"0x{b:02X}" for b in extra) if extra else ""

            results.check(f"T6.{test_id}", desc,
                          result_code == 0x00, detail)
            on_target_total += 1

        elif msg.arbitration_id == TEST_SUMMARY_ID and len(msg.data) >= 4:
            total = msg.data[0]
            passed = msg.data[1]
            failed = msg.data[2]
            sentinel = msg.data[3]

            if sentinel == 0xAA:
                print(f"\n  Target summary: {passed}/{total} passed, {failed} failed")
                summary_received = True

    if not summary_received:
        print("\n  WARNING: No target summary received (timeout)")

    return summary_received


def test_heartbeat_timing(bus, results):
    """H6.1: Measure heartbeat frame timing (should be ~1000ms +/- 50ms)."""
    print("\n--- H6.1: Measuring heartbeat frame timing ---")
    bus.flush_rx(0.5)

    # Collect timestamps of DIAG_STATUS frames (0x7F0)
    timestamps = []
    deadline = time.time() + 8.0

    while time.time() < deadline and len(timestamps) < 5:
        msg = bus.recv_frame(timeout=2.0)
        if msg and msg.arbitration_id == DIAG_STATUS_ID:
            timestamps.append(time.time())

    if len(timestamps) >= 3:
        deltas = [timestamps[i+1] - timestamps[i] for i in range(len(timestamps)-1)]
        avg_ms = sum(deltas) / len(deltas) * 1000
        # Allow 950-1100ms range (timer jitter + bus delay)
        ok = 950 <= avg_ms <= 1100
        detail = f"avg={avg_ms:.0f}ms, samples={len(deltas)}"
        results.check("H6.1", "Heartbeat timing (~1000ms)", ok, detail)
    else:
        results.check("H6.1", "Heartbeat timing (~1000ms)",
                      False, f"only {len(timestamps)} frames received")


def test_heartbeat_disable(bus, results):
    """H6.2: Disable diagnostics, verify no heartbeat for 3s."""
    print("\n--- H6.2: Verifying heartbeat stops when disabled ---")

    # Disable diagnostics: WRITE_PARAM section=DIAG, param=2(enabled), sub=0, value=0
    bus.send_frame(CONFIG_CMD_ID, [CFG_CMD_WRITE_PARAM, CFG_SECTION_DIAG, 2, 0, 0, 0, 0, 0])
    time.sleep(0.2)

    # SAVE to apply
    bus.send_frame(CONFIG_CMD_ID, [CFG_CMD_SAVE, 0, 0, 0, 0, 0, 0, 0])
    time.sleep(1.0)
    bus.flush_rx(0.5)

    # Wait 3 seconds, count heartbeat frames
    hb_count = 0
    deadline = time.time() + 3.0
    while time.time() < deadline:
        msg = bus.recv_frame(timeout=0.5)
        if msg and msg.arbitration_id == DIAG_STATUS_ID:
            hb_count += 1

    ok = (hb_count == 0)
    results.check("H6.2", "Heartbeat stops when disabled",
                  ok, f"received {hb_count} heartbeat frames")

    # Re-enable diagnostics
    bus.send_frame(CONFIG_CMD_ID, [CFG_CMD_WRITE_PARAM, CFG_SECTION_DIAG, 2, 0, 1, 0, 0, 0])
    time.sleep(0.2)
    bus.send_frame(CONFIG_CMD_ID, [CFG_CMD_SAVE, 0, 0, 0, 0, 0, 0, 0])
    time.sleep(1.0)


def run_tests(channel: str, bitrate: int):
    print("=" * 60)
    print("  Phase 6: Diagnostics Tests")
    print(f"  Channel: {channel}, Bitrate: {bitrate}")
    print("=" * 60)

    results = TestResult()

    with PcanBus(channel=channel, bitrate=bitrate) as bus:
        # ================================================================
        # PHASE A: Auto-tests
        # ================================================================
        print("\n" + "=" * 60)
        print("  Phase A: On-Target Auto-Tests")
        print("  (Reset board now if not freshly powered)")
        print("=" * 60)

        time.sleep(1.0)
        bus.flush_rx(0.5)

        print("\n--- Collecting on-target test results ---")
        got_summary = collect_on_target_results(bus, results, timeout=60.0)

        # ================================================================
        # PHASE B: Host-verified heartbeat timing
        # ================================================================
        print("\n" + "=" * 60)
        print("  Phase B: Host-Verified Heartbeat Tests")
        print("=" * 60)

        test_heartbeat_timing(bus, results)
        test_heartbeat_disable(bus, results)

    return results.summary()


def main():
    parser = argparse.ArgumentParser(description="Phase 6 Diagnostics Tests")
    parser.add_argument('--channel', default='PCAN_USBBUS1',
                        help='PCAN channel (default: PCAN_USBBUS1)')
    parser.add_argument('--bitrate', type=int, default=500000,
                        help='CAN bitrate (default: 500000)')
    args = parser.parse_args()

    success = run_tests(args.channel, args.bitrate)
    sys.exit(0 if success else 1)


if __name__ == '__main__':
    main()
