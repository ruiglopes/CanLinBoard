#!/usr/bin/env python3
"""
Phase 5 Host-Side Tests: Configuration System
===============================================

Collects on-target test results (T5.1-T5.12, T5.15-T5.16), then
optionally sends REBOOT and ENTER_BOOTLOADER commands for T5.13/T5.14.

Requires:
  - Phase 5 test firmware flashed (built with -DTEST_PHASE5)
  - PCAN adapter connected to CAN1

Test flow:
  Phase A — Auto-tests (on-target, results on 0x7FA/0x7FB)
    T5.1   First boot defaults
    T5.2   Config persistence
    T5.3   Ping-pong slot swap
    T5.4   CRC validation
    T5.5   CONNECT command
    T5.6   READ CAN1 bitrate
    T5.7   WRITE CAN1 bitrate
    T5.8   Read after write
    T5.9   SAVE + verify NVM
    T5.10  Bulk write routing
    T5.11  Bulk CRC mismatch
    T5.12  LOAD_DEFAULTS
    T5.15  Unknown param
    T5.16  Runtime apply

  Phase B — REBOOT test (host sends REBOOT, verifies board reconnects)
    T5.13  Board resets and summary re-appears

  Phase C — ENTER_BOOTLOADER test (host sends ENTER_BL, verifies 0x700 response)
    T5.14  Bootloader active (responds on 0x701)

Usage:
  python tests/phase5/test_nvm_config_host.py [--channel PCAN_USBBUS1]
  python tests/phase5/test_nvm_config_host.py --skip-reboot   # Skip T5.13/T5.14
"""

import sys
import os
import time
import argparse
import struct

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'common'))
from pcan_helper import PcanBus, TestResult

# CAN IDs
CONFIG_CMD_ID   = 0x600
CONFIG_RESP_ID  = 0x601
TEST_RESULT_ID  = 0x7FA
TEST_SUMMARY_ID = 0x7FB
BL_CMD_ID       = 0x700
BL_RESP_ID      = 0x701

# Config commands
CFG_CMD_CONNECT          = 0x01
CFG_CMD_REBOOT           = 0x04
CFG_CMD_ENTER_BOOTLOADER = 0x05

# Bootloader unlock key (LE)
RESET_UNLOCK_KEY = 0xB007CAFE

RESULT_NAMES = {0x00: "PASS", 0x01: "FAIL", 0x02: "SKIP"}

# On-target test descriptions
TEST_DESCRIPTIONS = {
    1:  "First boot defaults",
    2:  "Config persistence",
    3:  "Ping-pong slot swap",
    4:  "CRC validation",
    5:  "CONNECT command",
    6:  "READ CAN1 bitrate (default 500k)",
    7:  "WRITE CAN1 bitrate (250k)",
    8:  "Read after write (250k)",
    9:  "SAVE + verify NVM",
    10: "Bulk write routing (3 rules)",
    11: "Bulk CRC mismatch (rejected)",
    12: "LOAD_DEFAULTS",
    13: "REBOOT (host-verified)",
    14: "ENTER_BOOTLOADER (host-verified)",
    15: "Unknown param (invalid section)",
    16: "Runtime apply (CAN2 termination)",
}


def collect_on_target_results(bus, results, timeout=30.0):
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

            results.check(f"T5.{test_id}", desc,
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


def test_reboot(bus, results):
    """T5.13: Send REBOOT command, verify board reconnects."""
    print("\n--- T5.13: Sending REBOOT command ---")
    bus.flush_rx(0.2)

    # Send REBOOT via config protocol
    bus.send_frame(CONFIG_CMD_ID,
                   [CFG_CMD_REBOOT, 0, 0, 0, 0, 0, 0, 0])

    # Wait for reboot + re-init
    time.sleep(4.0)
    bus.flush_rx(0.5)

    # Look for test summary from rebooted firmware
    found_summary = False
    deadline = time.time() + 30.0
    while time.time() < deadline:
        msg = bus.recv_frame(timeout=1.0)
        if msg and msg.arbitration_id == TEST_SUMMARY_ID:
            if len(msg.data) >= 4 and msg.data[3] == 0xAA:
                print(f"  Board rebooted — summary: {msg.data[1]}/{msg.data[0]} passed")
                found_summary = True
                break

    results.check("T5.13", "REBOOT (board reconnects)",
                  found_summary,
                  "summary received" if found_summary else "no summary after reboot")


def test_enter_bootloader(bus, results):
    """T5.14: Send ENTER_BOOTLOADER, verify bootloader responds on 0x701."""
    print("\n--- T5.14: Sending ENTER_BOOTLOADER command ---")
    bus.flush_rx(0.2)

    # Send ENTER_BOOTLOADER with unlock key
    key = RESET_UNLOCK_KEY
    bus.send_frame(CONFIG_CMD_ID, [
        CFG_CMD_ENTER_BOOTLOADER,
        key & 0xFF, (key >> 8) & 0xFF,
        (key >> 16) & 0xFF, (key >> 24) & 0xFF,
        0, 0, 0
    ])

    # Wait for reboot into bootloader
    time.sleep(3.0)
    bus.flush_rx(0.2)

    # Send a bootloader ping (empty frame on 0x700) to check if BL is active
    bus.send_frame(BL_CMD_ID, [0x00, 0, 0, 0, 0, 0, 0, 0])

    # Look for bootloader response on 0x701
    bl_resp = bus.recv_until_id(BL_RESP_ID, timeout=2.0)
    found_bl = bl_resp is not None

    results.check("T5.14", "ENTER_BOOTLOADER (bootloader active)",
                  found_bl,
                  f"response on 0x701" if found_bl else "no bootloader response")


def run_tests(channel: str, bitrate: int, skip_reboot: bool):
    print("=" * 60)
    print("  Phase 5: Configuration System Tests")
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
        got_summary = collect_on_target_results(bus, results, timeout=30.0)

        # Also verify CONNECT via host
        print("\n--- Host-side CONNECT verification ---")
        bus.flush_rx(0.2)
        resp = bus.send_config_cmd(CFG_CMD_CONNECT)
        if resp and len(resp.data) >= 5:
            d = list(resp.data)
            cmd_echo = d[0]
            status = d[1]
            fw_major = d[2]
            fw_minor = d[3]
            fw_patch = d[4]
            results.check("T5.H1", f"CONNECT response (FW {fw_major}.{fw_minor}.{fw_patch})",
                          status == 0x00 and cmd_echo == CFG_CMD_CONNECT,
                          f"status=0x{status:02X}")
        else:
            results.check("T5.H1", "CONNECT response",
                          False, "no response on 0x601")

        if skip_reboot:
            print("\n  Skipping T5.13/T5.14 (--skip-reboot)")
        else:
            # ============================================================
            # PHASE B: REBOOT test
            # ============================================================
            print("\n" + "=" * 60)
            print("  Phase B: REBOOT Test")
            print("=" * 60)
            test_reboot(bus, results)

            # Drain post-reboot auto-test results (we don't re-validate them)
            time.sleep(5.0)
            bus.flush_rx(2.0)

            # ============================================================
            # PHASE C: ENTER_BOOTLOADER test
            # ============================================================
            print("\n" + "=" * 60)
            print("  Phase C: ENTER_BOOTLOADER Test")
            print("=" * 60)
            test_enter_bootloader(bus, results)

    return results.summary()


def main():
    parser = argparse.ArgumentParser(description="Phase 5 Configuration System Tests")
    parser.add_argument('--channel', default='PCAN_USBBUS1',
                        help='PCAN channel (default: PCAN_USBBUS1)')
    parser.add_argument('--bitrate', type=int, default=500000,
                        help='CAN bitrate (default: 500000)')
    parser.add_argument('--skip-reboot', action='store_true',
                        help='Skip T5.13/T5.14 (reboot/bootloader tests)')
    args = parser.parse_args()

    success = run_tests(args.channel, args.bitrate, args.skip_reboot)
    sys.exit(0 if success else 1)


if __name__ == '__main__':
    main()
