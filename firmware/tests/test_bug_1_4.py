#!/usr/bin/env python3
"""
Bug 1.4 Regression Test: CAN2 not stopped before restart
=========================================================

Tests that re-applying config with CAN2 enabled does not corrupt
PIO state. The board must survive multiple save cycles with CAN2 on.

Requires:
  - Main firmware flashed (with fix)
  - PCAN adapter connected to CAN1

Test cases:
  1. Enable CAN2, save → board alive
  2. Save again (CAN2 re-started while running) → board alive
  3. Third save (stress) → board alive
  4. Disable CAN2, save → board alive, clean state

Usage:
  python tests/test_bug_1_4.py [--channel PCAN_USBBUS1]
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
CFG_CMD_SAVE        = 0x02
CFG_CMD_DEFAULTS    = 0x03
CFG_CMD_WRITE_PARAM = 0x11

CFG_SECTION_CAN  = 0x00
CFG_STATUS_OK    = 0x00


def wait_response(bus, timeout=1.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        msg = bus.recv_frame(timeout=0.1)
        if msg and msg.arbitration_id == CONFIG_RESP_ID:
            return msg
    return None


def send_cmd(bus, data, label="cmd"):
    bus.flush_rx(0.1)
    bus.send_frame(CONFIG_CMD_ID, data)
    resp = wait_response(bus)
    if resp is None:
        print(f"    {label}: no response!")
        return False
    if resp.data[1] != CFG_STATUS_OK:
        print(f"    {label}: status=0x{resp.data[1]:02X}")
        return False
    return True


def board_alive(bus):
    bus.flush_rx(0.2)
    bus.send_frame(CONFIG_CMD_ID, [CFG_CMD_CONNECT])
    return wait_response(bus, timeout=2.0) is not None


def main():
    parser = argparse.ArgumentParser(description="Bug 1.4 regression test")
    parser.add_argument('--channel', default='PCAN_USBBUS1')
    args = parser.parse_args()

    print("=" * 58)
    print("Bug 1.4: CAN2 restart must stop PIO first")
    print("=" * 58)

    passed = 0
    failed = 0

    with PcanBus(channel=args.channel) as bus:
        print("\n  Connecting...", end=" ")
        if not board_alive(bus):
            print("FAIL — board not responding")
            return 1
        print("OK")

        # Load defaults for clean state
        send_cmd(bus, [CFG_CMD_DEFAULTS], "defaults")

        # Enable CAN2: WRITE_PARAM section=CAN, param=2 (enabled), sub=1 (CAN2), value=1
        print("\n  T1: Enable CAN2 + save...", end=" ")
        if not send_cmd(bus, [CFG_CMD_WRITE_PARAM, CFG_SECTION_CAN, 2, 1, 1], "enable CAN2"):
            print("FAIL")
            failed += 1
        elif not send_cmd(bus, [CFG_CMD_SAVE], "save"):
            print("FAIL")
            failed += 1
        else:
            time.sleep(0.5)
            if board_alive(bus):
                print("PASS")
                passed += 1
            else:
                print("FAIL — board crashed")
                failed += 1
                return 1

        # Save again — CAN2 already running, will be restarted
        print("  T2: Save again (CAN2 restart while running)...", end=" ")
        if not send_cmd(bus, [CFG_CMD_SAVE], "save"):
            print("FAIL")
            failed += 1
        else:
            time.sleep(0.5)
            if board_alive(bus):
                print("PASS")
                passed += 1
            else:
                print("FAIL — board crashed on restart")
                failed += 1
                return 1

        # Third save for stress
        print("  T3: Third save (stress)...", end=" ")
        if not send_cmd(bus, [CFG_CMD_SAVE], "save"):
            print("FAIL")
            failed += 1
        else:
            time.sleep(0.5)
            if board_alive(bus):
                print("PASS")
                passed += 1
            else:
                print("FAIL — board crashed")
                failed += 1
                return 1

        # Disable CAN2, restore clean state
        print("  T4: Disable CAN2 + save (cleanup)...", end=" ")
        send_cmd(bus, [CFG_CMD_WRITE_PARAM, CFG_SECTION_CAN, 2, 1, 0], "disable CAN2")
        if not send_cmd(bus, [CFG_CMD_SAVE], "save"):
            print("FAIL")
            failed += 1
        else:
            time.sleep(0.5)
            if board_alive(bus):
                print("PASS")
                passed += 1
            else:
                print("FAIL")
                failed += 1

    print(f"\n{'=' * 58}")
    print(f"Results: {passed}/4 passed, {failed} failed")
    print(f"{'=' * 58}")
    return 0 if failed == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
