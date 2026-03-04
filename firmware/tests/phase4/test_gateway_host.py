#!/usr/bin/env python3
"""
Phase 4 Host-Side Test Collector: Gateway Engine
=================================================

Listens on PCAN for test results from the Phase 4 on-target test firmware.

The target runs auto-tests on boot and reports results on CAN1 ID 0x7FA.

Tests verified:
  T4.1   CAN1→CAN1 passthrough
  T4.2   CAN1→CAN2 routing (stats-based)
  T4.3   ID translation
  T4.4   ID mask match (0xFF0)
  T4.5   No match → drop
  T4.6   Full passthrough (0 mappings, 8 bytes)
  T4.7   Single byte extraction
  T4.8   Mask + shift (nibble)
  T4.9   CAN→LIN routing (stats-based)
  T4.10  LIN→CAN routing (synthetic injection)
  T4.11  Fan-out (2 rules, 1 input → 2 outputs)
  T4.12  Disabled rule (no output)
  T4.13  DLC override
  T4.14  Multiple byte mappings (3 simultaneous)
  T4.15  Offset mapping (+10)
  T4.16  Gateway stats consistency

Usage:
  1. Flash Phase 4 test firmware (built with -DTEST_PHASE4)
  2. Connect PCAN to CAN1
  3. Run: python tests/phase4/test_gateway_host.py [--channel PCAN_USBBUS1]
  4. Power/reset the board
  5. Auto-tests run immediately; results appear within ~15 seconds
"""

import sys
import os
import time
import argparse

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'common'))
from pcan_helper import PcanBus, TestResult

TEST_RESULT_ID = 0x7FA
TEST_SUMMARY_ID = 0x7FB

RESULT_NAMES = {0x00: "PASS", 0x01: "FAIL", 0x02: "SKIP"}

TEST_DESCRIPTIONS = {
    1:  "CAN1→CAN1 passthrough",
    2:  "CAN1→CAN2 routing",
    3:  "ID translation (0x101→0x300)",
    4:  "ID mask match (0xFF0)",
    5:  "No match → drop",
    6:  "Full passthrough (0 mappings)",
    7:  "Single byte extraction (src[2]→dst[0])",
    8:  "Mask + shift (low nibble→high nibble)",
    9:  "CAN→LIN routing",
    10: "LIN→CAN routing (synthetic)",
    11: "Fan-out (2 rules)",
    12: "Disabled rule",
    13: "DLC override (8→4)",
    14: "Multiple byte mappings (3x)",
    15: "Offset mapping (+10)",
    16: "Gateway stats consistency",
}


def decode_test_extra(test_id, extra):
    """Decode test-specific extra bytes into human-readable string."""
    if not extra:
        return ""

    if test_id == 1:
        got = extra[0] if len(extra) > 0 else 0
        id_hi = extra[1] if len(extra) > 1 else 0
        id_lo = extra[2] if len(extra) > 2 else 0
        d0 = extra[3] if len(extra) > 3 else 0
        out_id = (id_hi << 8) | id_lo
        return f"received={got}, out_id=0x{out_id:03X}, data[0]=0x{d0:02X}"

    if test_id == 2:
        routed = extra[0] if len(extra) > 0 else 0
        dropped = extra[1] if len(extra) > 1 else 0
        return f"routed={routed}, dropped={dropped}"

    if test_id in (3, 4):
        got = extra[0] if len(extra) > 0 else 0
        id_hi = extra[1] if len(extra) > 1 else 0
        id_lo = extra[2] if len(extra) > 2 else 0
        out_id = (id_hi << 8) | id_lo
        return f"received={got}, out_id=0x{out_id:03X}"

    if test_id == 5:
        routed = extra[0] if len(extra) > 0 else 0
        dropped = extra[1] if len(extra) > 1 else 0
        return f"routed={routed}, dropped={dropped}"

    if test_id == 6:
        got = extra[0] if len(extra) > 0 else 0
        match = extra[1] if len(extra) > 1 else 0
        d0 = extra[2] if len(extra) > 2 else 0
        d7 = extra[3] if len(extra) > 3 else 0
        return f"received={got}, data_match={match}, d[0]=0x{d0:02X}, d[7]=0x{d7:02X}"

    if test_id == 7:
        got = extra[0] if len(extra) > 0 else 0
        d0 = extra[1] if len(extra) > 1 else 0
        dlc = extra[2] if len(extra) > 2 else 0
        return f"received={got}, dst[0]=0x{d0:02X}, dlc={dlc}"

    if test_id == 8:
        got = extra[0] if len(extra) > 0 else 0
        val = extra[1] if len(extra) > 1 else 0
        return f"received={got}, result=0x{val:02X} (expected 0x50)"

    if test_id == 9:
        routed = extra[0] if len(extra) > 0 else 0
        dropped = extra[1] if len(extra) > 1 else 0
        overflow = extra[2] if len(extra) > 2 else 0
        return f"routed={routed}, dropped={dropped}, lin_overflow={overflow}"

    if test_id == 10:
        got = extra[0] if len(extra) > 0 else 0
        id_hi = extra[1] if len(extra) > 1 else 0
        id_lo = extra[2] if len(extra) > 2 else 0
        d0 = extra[3] if len(extra) > 3 else 0
        out_id = (id_hi << 8) | id_lo
        return f"received={got}, out_id=0x{out_id:03X}, data[0]=0x{d0:02X}"

    if test_id == 11:
        got1 = extra[0] if len(extra) > 0 else 0
        got2 = extra[1] if len(extra) > 1 else 0
        id260 = extra[2] if len(extra) > 2 else 0
        id360 = extra[3] if len(extra) > 3 else 0
        return f"frame1={got1}, frame2={got2}, 0x260={id260}, 0x360={id360}"

    if test_id == 12:
        got = extra[0] if len(extra) > 0 else 0
        routed = extra[1] if len(extra) > 1 else 0
        dropped = extra[2] if len(extra) > 2 else 0
        return f"received={got}, routed={routed}, dropped={dropped}"

    if test_id == 13:
        got = extra[0] if len(extra) > 0 else 0
        dlc = extra[1] if len(extra) > 1 else 0
        return f"received={got}, dlc={dlc}"

    if test_id == 14:
        got = extra[0] if len(extra) > 0 else 0
        d0 = extra[1] if len(extra) > 1 else 0
        d1 = extra[2] if len(extra) > 2 else 0
        d2 = extra[3] if len(extra) > 3 else 0
        return f"received={got}, d[0]=0x{d0:02X}, d[1]=0x{d1:02X}, d[2]=0x{d2:02X}"

    if test_id == 15:
        got = extra[0] if len(extra) > 0 else 0
        val = extra[1] if len(extra) > 1 else 0
        exp = extra[2] if len(extra) > 2 else 0
        return f"received={got}, result=0x{val:02X}, expected=0x{exp:02X}"

    if test_id == 16:
        routed = extra[0] if len(extra) > 0 else 0
        dropped = extra[1] if len(extra) > 1 else 0
        can_ovf = extra[2] if len(extra) > 2 else 0
        lin_ovf = extra[3] if len(extra) > 3 else 0
        return f"routed={routed}, dropped={dropped}, can_ovf={can_ovf}, lin_ovf={lin_ovf}"

    return ' '.join(f"0x{b:02X}" for b in extra)


def run_collection(channel: str, bitrate: int, timeout: float):
    print("=" * 60)
    print("  Phase 4: Gateway Engine Tests — Collecting Results")
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

                desc = TEST_DESCRIPTIONS.get(test_id, f"Unknown test {test_id}")
                detail = decode_test_extra(test_id, extra)

                if result_code == 0x02:  # SKIP
                    print(f"  [SKIP] T4.{test_id}: {desc} — {detail}")
                else:
                    results.check(f"T4.{test_id}", desc,
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
            print("    - Test firmware not flashed (need -DTEST_PHASE4)")

    return results.summary()


def main():
    parser = argparse.ArgumentParser(description="Phase 4 Gateway Engine Test Collector")
    parser.add_argument('--channel', default='PCAN_USBBUS1',
                        help='PCAN channel (default: PCAN_USBBUS1)')
    parser.add_argument('--bitrate', type=int, default=500000,
                        help='CAN bitrate (default: 500000)')
    parser.add_argument('--timeout', type=float, default=30.0,
                        help='Collection timeout in seconds (default: 30)')
    args = parser.parse_args()

    success = run_collection(args.channel, args.bitrate, args.timeout)
    sys.exit(0 if success else 1)


if __name__ == '__main__':
    main()
