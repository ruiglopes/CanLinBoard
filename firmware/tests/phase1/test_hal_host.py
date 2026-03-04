#!/usr/bin/env python3
"""
Phase 1 Host-Side Test Collector: HAL Verification
===================================================

Listens on PCAN for test results from the Phase 1 on-target test firmware.

The target sends:
  - ID 0x7FA: Individual test results
  - ID 0x7FB: Final summary

Usage:
  1. Flash the Phase 1 test firmware (built with -DTEST_PHASE1)
  2. Connect PCAN to CAN1
  3. Run: python tests/phase1/test_hal_host.py [--channel PCAN_USBBUS1]
  4. Power/reset the board
  5. Results appear within ~5 seconds
"""

import sys
import os
import argparse
import time

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'common'))
from pcan_helper import PcanBus, TestResult

TEST_DIAG_ID = 0x7FD
TEST_RESULT_ID = 0x7FA
TEST_SUMMARY_ID = 0x7FB

RESULT_NAMES = {0x00: "PASS", 0x01: "FAIL", 0x02: "SKIP"}

TEST_DESCRIPTIONS = {
    1:  "CAN1 EN pin enable (pin LOW)",
    2:  "CAN1 EN pin disable (pin HIGH)",
    3:  "CAN1 TERM pin enable (pin HIGH)",
    4:  "CAN2 EN + TERM pins (all 4 checks)",
    5:  "SPI0 initialization",
    6:  "SJA1124 ID register read",
    7:  "SJA1124 register write/readback",
    8:  "NVM write 256 bytes",
    9:  "NVM readback verification",
    10: "NVM erase verification",
    11: "Clock output init (manual scope check)",
    12: "Bootloader entry (board reboots)",
}


def run_collection(channel: str, bitrate: int, timeout: float):
    print("=" * 60)
    print("  Phase 1: HAL Test — Waiting for target results")
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

            if msg.arbitration_id == TEST_DIAG_ID and len(msg.data) >= 4:
                clk_hz = (msg.data[0] << 24) | (msg.data[1] << 16) | (msg.data[2] << 8) | msg.data[3]
                print(f"  [DIAG] System clock: {clk_hz} Hz ({clk_hz / 1_000_000:.1f} MHz)")

            elif msg.arbitration_id == TEST_RESULT_ID and len(msg.data) >= 2:
                test_id = msg.data[0]
                result_code = msg.data[1]
                extra = list(msg.data[2:msg.dlc]) if msg.dlc > 2 else []

                result_str = RESULT_NAMES.get(result_code, f"0x{result_code:02X}")
                desc = TEST_DESCRIPTIONS.get(test_id, f"Unknown test {test_id}")
                detail = ""

                # Decode test-specific extra data
                if test_id == 4 and extra:
                    bits = extra[0]
                    detail = f"EN_on={bits&1} EN_off={bits>>1&1} TERM_on={bits>>2&1} TERM_off={bits>>3&1}"
                elif test_id == 6 and len(extra) >= 2:
                    detail = f"spi_ok={extra[0]}, id=0x{extra[1]:02X}"
                elif test_id == 7 and len(extra) >= 2:
                    detail = f"wrote=0x{extra[0]:02X}, read=0x{extra[1]:02X}"
                elif test_id == 9 and len(extra) >= 2:
                    detail = f"mismatches={extra[0]}, first_bad_idx={extra[1]}"
                elif test_id == 10 and extra:
                    detail = f"non_FF_bytes={extra[0]}"
                elif test_id == 12 and extra:
                    detail = "board will reboot to bootloader"

                results.check(f"T1.{test_id}", desc,
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
            print("\n  WARNING: No summary frame received (timeout)")
            print("  Possible causes:")
            print("    - Board not powered/reset")
            print("    - Wrong CAN channel/bitrate")
            print("    - Test firmware not flashed (need -DTEST_PHASE1)")

    return results.summary()


def main():
    parser = argparse.ArgumentParser(description="Phase 1 HAL Test Collector")
    parser.add_argument('--channel', default='PCAN_USBBUS1',
                        help='PCAN channel (default: PCAN_USBBUS1)')
    parser.add_argument('--bitrate', type=int, default=500000,
                        help='CAN bitrate (default: 500000)')
    parser.add_argument('--timeout', type=float, default=15.0,
                        help='Collection timeout in seconds (default: 15)')
    args = parser.parse_args()

    success = run_collection(args.channel, args.bitrate, args.timeout)
    sys.exit(0 if success else 1)


if __name__ == '__main__':
    main()
