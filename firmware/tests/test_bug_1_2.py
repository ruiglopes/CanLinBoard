#!/usr/bin/env python3
"""
Bug 1.2 Regression Test: Diagnostics on disabled CAN2
======================================================

Tests that setting diag.bus=1 (CAN2) while CAN2 is disabled does not
crash the board. The board should silently drop heartbeat transmits
and remain responsive on CAN1.

Requires:
  - Main firmware flashed
  - PCAN adapter connected to CAN1
  - CAN2 NOT connected / disabled (default config)

Test cases:
  1. Set diag.bus=1 (CAN2), save, verify board stays alive
  2. Restore diag.bus=0 (CAN1), save, verify heartbeats resume on CAN1

Usage:
  python tests/test_bug_1_2.py [--channel PCAN_USBBUS1]
"""

import sys
import os
import time
import argparse

sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'common'))
from pcan_helper import PcanBus

CONFIG_CMD_ID   = 0x600
CONFIG_RESP_ID  = 0x601
DIAG_BASE_ID    = 0x7F0  # default

CFG_CMD_CONNECT     = 0x01
CFG_CMD_SAVE        = 0x02
CFG_CMD_WRITE_PARAM = 0x11
CFG_CMD_REBOOT      = 0x04
CFG_CMD_DEFAULTS    = 0x03

CFG_SECTION_DIAG = 0x03
CFG_STATUS_OK    = 0x00

RESET_UNLOCK_KEY = 0xB007CAFE


def wait_response(bus, timeout=1.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        msg = bus.recv_frame(timeout=0.1)
        if msg and msg.arbitration_id == CONFIG_RESP_ID:
            return msg
    return None


def send_cmd(bus, data, label="cmd"):
    """Send a config command and verify OK response."""
    bus.flush_rx(0.1)
    bus.send_frame(CONFIG_CMD_ID, data)
    resp = wait_response(bus)
    if resp is None:
        print(f"    {label}: no response!")
        return False
    if resp.data[1] != CFG_STATUS_OK:
        print(f"    {label}: status=0x{resp.data[1]:02X} (expected OK)")
        return False
    return True


def board_alive(bus):
    """Check if board responds to CONNECT."""
    bus.flush_rx(0.2)
    bus.send_frame(CONFIG_CMD_ID, [CFG_CMD_CONNECT])
    resp = wait_response(bus, timeout=2.0)
    return resp is not None


def wait_heartbeat(bus, timeout=3.0):
    """Wait for a diagnostic heartbeat frame on CAN1."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        msg = bus.recv_frame(timeout=0.1)
        if msg and DIAG_BASE_ID <= msg.arbitration_id <= DIAG_BASE_ID + 4:
            return msg
    return None


def test_diag_on_disabled_can2(bus):
    """T1: Set diag.bus=CAN2 while CAN2 is disabled. Board must not crash."""
    print("\n  T1: Set diag.bus=1 (CAN2) with CAN2 disabled...", end=" ")

    # Write diag.bus = 1 (CAN2)
    # WRITE_PARAM: [cmd, section, param, sub, value...]
    if not send_cmd(bus, [CFG_CMD_WRITE_PARAM, CFG_SECTION_DIAG, 3, 0, 1], "write diag.bus=1"):
        print("FAIL — could not write param")
        return False

    # Save to NVM
    if not send_cmd(bus, [CFG_CMD_SAVE], "save"):
        print("FAIL — could not save")
        return False

    # Wait a few seconds for diagnostics task to attempt heartbeats on CAN2
    time.sleep(3.0)

    # Board should still be alive on CAN1
    if board_alive(bus):
        print("PASS (board still responsive after 3s)")
        return True
    else:
        print("FAIL — board not responding (likely crashed)")
        return False


def test_restore_diag_bus(bus):
    """T2: Restore diag.bus=0 (CAN1), verify heartbeats resume."""
    print("  T2: Restore diag.bus=0 (CAN1)...", end=" ")

    # Write diag.bus = 0 (CAN1)
    if not send_cmd(bus, [CFG_CMD_WRITE_PARAM, CFG_SECTION_DIAG, 3, 0, 0], "write diag.bus=0"):
        print("FAIL — could not write param")
        return False

    # Save
    if not send_cmd(bus, [CFG_CMD_SAVE], "save"):
        print("FAIL — could not save")
        return False

    # Wait for heartbeats to resume on CAN1
    hb = wait_heartbeat(bus, timeout=5.0)
    if hb:
        print(f"PASS (heartbeat 0x{hb.arbitration_id:03X} received)")
        return True
    else:
        print("FAIL — no heartbeat on CAN1")
        return False


def main():
    parser = argparse.ArgumentParser(description="Bug 1.2 regression test")
    parser.add_argument('--channel', default='PCAN_USBBUS1')
    args = parser.parse_args()

    print("=" * 55)
    print("Bug 1.2: Diagnostics on disabled CAN2 must not crash")
    print("=" * 55)

    passed = 0
    failed = 0

    with PcanBus(channel=args.channel) as bus:
        # Connect
        print("\n  Connecting to board...", end=" ")
        if not board_alive(bus):
            print("FAIL — board not responding")
            return 1
        print("OK")

        # Load defaults first to ensure clean state
        send_cmd(bus, [CFG_CMD_DEFAULTS], "load defaults")
        send_cmd(bus, [CFG_CMD_SAVE], "save defaults")

        if test_diag_on_disabled_can2(bus):
            passed += 1
        else:
            failed += 1

        if test_restore_diag_bus(bus):
            passed += 1
        else:
            failed += 1

    print(f"\n{'=' * 55}")
    print(f"Results: {passed}/2 passed, {failed} failed")
    print(f"{'=' * 55}")
    return 0 if failed == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
