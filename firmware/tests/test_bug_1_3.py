#!/usr/bin/env python3
"""
Bug 1.3 Regression Test: Zero bitrate divide-by-zero
=====================================================

Tests that the config protocol rejects invalid bitrate values
for both CAN and LIN, preventing divide-by-zero in can2040 and SJA1124.

Requires:
  - Main firmware flashed (with fix)
  - PCAN adapter connected to CAN1

Test cases:
  1. CAN bitrate = 0        → rejected
  2. CAN bitrate = 5000     → rejected (below 10000 min)
  3. CAN bitrate = 2000000  → rejected (above 1000000 max)
  4. CAN bitrate = 500000   → accepted (valid)
  5. LIN bitrate = 0        → rejected
  6. LIN bitrate = 500      → rejected (below 1000 min)
  7. LIN bitrate = 50000    → rejected (above 20000 max)
  8. LIN bitrate = 19200    → accepted (valid)

Usage:
  python tests/test_bug_1_3.py [--channel PCAN_USBBUS1]
"""

import sys
import os
import time
import argparse

sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'common'))
from pcan_helper import PcanBus

CONFIG_CMD_ID   = 0x600
CONFIG_RESP_ID  = 0x601

CFG_CMD_CONNECT     = 0x01
CFG_CMD_WRITE_PARAM = 0x11

CFG_SECTION_CAN  = 0x00
CFG_SECTION_LIN  = 0x01
CFG_STATUS_OK    = 0x00
CFG_STATUS_INVALID_PARAM = 0x02


def wait_response(bus, timeout=1.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        msg = bus.recv_frame(timeout=0.1)
        if msg and msg.arbitration_id == CONFIG_RESP_ID:
            return msg
    return None


def write_bitrate(bus, section, sub, bitrate):
    """Send WRITE_PARAM for a bitrate value. Returns response status byte."""
    b = bitrate.to_bytes(3, 'little')
    bus.flush_rx(0.1)
    bus.send_frame(CONFIG_CMD_ID, [CFG_CMD_WRITE_PARAM, section, 0 if section == CFG_SECTION_CAN else 2, sub, b[0], b[1], b[2]])
    resp = wait_response(bus)
    if resp is None:
        return None
    return resp.data[1]


def run_test(bus, label, section, sub, bitrate, expect_ok):
    """Run a single bitrate validation test."""
    status = write_bitrate(bus, section, sub, bitrate)
    if status is None:
        print(f"  {label}: FAIL — no response")
        return False
    if expect_ok and status == CFG_STATUS_OK:
        print(f"  {label}: PASS (accepted)")
        return True
    elif not expect_ok and status == CFG_STATUS_INVALID_PARAM:
        print(f"  {label}: PASS (rejected)")
        return True
    elif expect_ok and status != CFG_STATUS_OK:
        print(f"  {label}: FAIL — expected OK, got 0x{status:02X}")
        return False
    else:
        print(f"  {label}: FAIL — expected reject, got 0x{status:02X}")
        return False


def main():
    parser = argparse.ArgumentParser(description="Bug 1.3 regression test")
    parser.add_argument('--channel', default='PCAN_USBBUS1')
    args = parser.parse_args()

    print("=" * 55)
    print("Bug 1.3: Zero/invalid bitrate must be rejected")
    print("=" * 55)

    passed = 0
    failed = 0
    total = 8

    with PcanBus(channel=args.channel) as bus:
        # Connect
        print("\n  Connecting...", end=" ")
        bus.flush_rx(0.3)
        bus.send_frame(CONFIG_CMD_ID, [CFG_CMD_CONNECT])
        if wait_response(bus, 2.0) is None:
            print("FAIL — board not responding")
            return 1
        print("OK\n")

        tests = [
            ("T1: CAN  bitrate=0",        CFG_SECTION_CAN, 0,       0, False),
            ("T2: CAN  bitrate=5000",      CFG_SECTION_CAN, 0,    5000, False),
            ("T3: CAN  bitrate=2000000",   CFG_SECTION_CAN, 0, 2000000, False),
            ("T4: CAN  bitrate=500000",    CFG_SECTION_CAN, 0,  500000, True),
            ("T5: LIN  bitrate=0",         CFG_SECTION_LIN, 0,       0, False),
            ("T6: LIN  bitrate=500",       CFG_SECTION_LIN, 0,     500, False),
            ("T7: LIN  bitrate=50000",     CFG_SECTION_LIN, 0,   50000, False),
            ("T8: LIN  bitrate=19200",     CFG_SECTION_LIN, 0,   19200, True),
        ]

        for label, section, sub, bitrate, expect_ok in tests:
            if run_test(bus, label, section, sub, bitrate, expect_ok):
                passed += 1
            else:
                failed += 1

    print(f"\n{'=' * 55}")
    print(f"Results: {passed}/{total} passed, {failed} failed")
    print(f"{'=' * 55}")
    return 0 if failed == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
