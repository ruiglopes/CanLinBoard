#!/usr/bin/env python3
"""
Bug 1.1 Regression Test: Bootloader entry requires unlock key
=============================================================

Tests that the config protocol rejects ENTER_BOOTLOADER commands
without a valid unlock key.

Requires:
  - Main firmware flashed (NOT test firmware)
  - PCAN adapter connected to CAN1
  - Board powered and running

Test cases:
  1. Short frame [0x05] (dlc=1) → must be rejected
  2. Frame with wrong key       → must be rejected
  3. Frame with correct key     → must be accepted (board enters bootloader)

Usage:
  python tests/test_bug_1_1.py [--channel PCAN_USBBUS1] [--skip-bootloader]
"""

import sys
import os
import time
import argparse

sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'common'))
from pcan_helper import PcanBus

CONFIG_CMD_ID   = 0x600
CONFIG_RESP_ID  = 0x601
BL_RESP_ID      = 0x701
CFG_CMD_ENTER_BOOTLOADER = 0x05
CFG_STATUS_OK            = 0x00
CFG_STATUS_INVALID_PARAM = 0x02
RESET_UNLOCK_KEY         = 0xB007CAFE


def key_bytes(key):
    """Return 4-byte little-endian key as a list."""
    return [key & 0xFF, (key >> 8) & 0xFF, (key >> 16) & 0xFF, (key >> 24) & 0xFF]


def wait_response(bus, timeout=1.0):
    """Wait for a config response on 0x601."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        msg = bus.recv_frame(timeout=0.1)
        if msg and msg.arbitration_id == CONFIG_RESP_ID:
            return msg
    return None


def test_short_frame_rejected(bus):
    """T1: Short frame [0x05] must be rejected with INVALID_PARAM."""
    print("\n  T1: Short frame (dlc=1, no key)...", end=" ")
    bus.flush_rx(0.2)
    bus.send_frame(CONFIG_CMD_ID, [CFG_CMD_ENTER_BOOTLOADER])

    resp = wait_response(bus)
    if resp is None:
        # No response at all is also acceptable — means it was silently dropped.
        # But if the board rebooted (no heartbeat), that's a FAIL.
        time.sleep(0.5)
        bus.flush_rx(0.1)
        # Check if board is still alive by sending a CONNECT
        bus.send_frame(CONFIG_CMD_ID, [0x01])  # CONNECT
        alive = wait_response(bus, timeout=1.0)
        if alive:
            print("PASS (no response, board still running)")
            return True
        else:
            print("FAIL — board appears to have rebooted!")
            return False

    if resp.data[0] == CFG_CMD_ENTER_BOOTLOADER and resp.data[1] == CFG_STATUS_INVALID_PARAM:
        print("PASS (rejected with INVALID_PARAM)")
        return True
    elif resp.data[0] == CFG_CMD_ENTER_BOOTLOADER and resp.data[1] == CFG_STATUS_OK:
        print("FAIL — accepted without key!")
        return False
    else:
        print(f"FAIL — unexpected response: {resp.data.hex()}")
        return False


def test_wrong_key_rejected(bus):
    """T2: Frame with wrong key must be rejected."""
    print("  T2: Wrong unlock key...", end=" ")
    bus.flush_rx(0.2)
    wrong_key = 0xDEADBEEF
    bus.send_frame(CONFIG_CMD_ID, [CFG_CMD_ENTER_BOOTLOADER] + key_bytes(wrong_key))

    resp = wait_response(bus)
    if resp is None:
        print("FAIL — no response")
        return False
    if resp.data[0] == CFG_CMD_ENTER_BOOTLOADER and resp.data[1] == CFG_STATUS_INVALID_PARAM:
        print("PASS (rejected with INVALID_PARAM)")
        return True
    elif resp.data[1] == CFG_STATUS_OK:
        print("FAIL — accepted wrong key!")
        return False
    else:
        print(f"FAIL — unexpected response: {resp.data.hex()}")
        return False


def test_correct_key_accepted(bus):
    """T3: Frame with correct key must be accepted (enters bootloader)."""
    print("  T3: Correct unlock key...", end=" ")
    bus.flush_rx(0.2)
    bus.send_frame(CONFIG_CMD_ID,
                   [CFG_CMD_ENTER_BOOTLOADER] + key_bytes(RESET_UNLOCK_KEY) + [0, 0, 0])

    resp = wait_response(bus)
    if resp is None:
        print("FAIL — no response")
        return False
    if resp.data[0] == CFG_CMD_ENTER_BOOTLOADER and resp.data[1] == CFG_STATUS_OK:
        print("PASS (accepted, entering bootloader)")
        # Wait for bootloader to come up
        time.sleep(2.0)
        return True
    else:
        print(f"FAIL — unexpected response: {resp.data.hex()}")
        return False


def main():
    parser = argparse.ArgumentParser(description="Bug 1.1 regression test")
    parser.add_argument('--channel', default='PCAN_USBBUS1')
    parser.add_argument('--skip-bootloader', action='store_true',
                        help='Skip T3 (correct key test that enters bootloader)')
    args = parser.parse_args()

    print("=" * 50)
    print("Bug 1.1: Bootloader entry requires unlock key")
    print("=" * 50)

    passed = 0
    failed = 0
    total = 3 if not args.skip_bootloader else 2

    with PcanBus(channel=args.channel) as bus:
        # First, send CONNECT to make sure board is alive
        print("\n  Connecting to board...", end=" ")
        bus.flush_rx(0.3)
        bus.send_frame(CONFIG_CMD_ID, [0x01])  # CONNECT
        resp = wait_response(bus, timeout=2.0)
        if resp is None:
            print("FAIL — no response from board. Is it powered and running?")
            return 1
        print("OK")

        # T1: Short frame
        if test_short_frame_rejected(bus):
            passed += 1
        else:
            failed += 1

        # T2: Wrong key
        if test_wrong_key_rejected(bus):
            passed += 1
        else:
            failed += 1

        # T3: Correct key (optional — enters bootloader)
        if not args.skip_bootloader:
            if test_correct_key_accepted(bus):
                passed += 1
            else:
                failed += 1

    print(f"\n{'=' * 50}")
    print(f"Results: {passed}/{total} passed, {failed} failed")
    print(f"{'=' * 50}")
    return 0 if failed == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
