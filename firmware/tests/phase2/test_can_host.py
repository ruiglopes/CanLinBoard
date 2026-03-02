#!/usr/bin/env python3
"""
Phase 2 Host-Side Tests: CAN Subsystem
=======================================

Requires:
  - Phase 2 test firmware flashed (built with -DTEST_PHASE2)
  - PCAN adapter connected to CAN1 (and optionally CAN2 via second adapter)

Tests:
  T2.1   CAN1 RX single frame
  T2.2   CAN1 TX single frame (echo test)
  T2.3   CAN2 enable + RX (if second PCAN available)
  T2.4   CAN2 TX (via loopback command)
  T2.5   Dual bus simultaneous (if second PCAN available)
  T2.6   CAN2 disable
  T2.7   CAN2 re-enable
  T2.8   Config ID filtering (0x600 routed to config queue)
  T2.9   Non-config ID routing (routed to gateway queue)
  T2.10  Stress test (100 frames rapid-fire)
  T2.11  RX latency estimation
  T2.12  Bitrate change (250 kbps)

Usage:
  python tests/phase2/test_can_host.py [--channel PCAN_USBBUS1] [--channel2 PCAN_USBBUS2]
"""

import sys
import os
import time
import argparse
import struct

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'common'))
from pcan_helper import PcanBus, TestResult

TEST_CMD_ID = 0x7F0
TEST_STATS_ID = 0x7F1
CONFIG_CMD_ID = 0x600
CONFIG_RESP_ID = 0x601
ECHO_OFFSET = 0x100


def get_stats(bus):
    """Send stats request and parse response."""
    bus.flush_rx(0.05)
    bus.send_frame(TEST_CMD_ID, [0x01, 0, 0, 0, 0, 0, 0, 0])
    msg = bus.recv_until_id(TEST_STATS_ID, timeout=1.0)
    if msg and len(msg.data) >= 8:
        return {
            'can1_rx': msg.data[0],
            'can1_tx': msg.data[1],
            'can1_err': msg.data[2],
            'can1_ovf': msg.data[3],
            'can2_rx': msg.data[4],
            'can2_tx': msg.data[5],
            'can2_state': msg.data[6],
            'can2_err': msg.data[7],
        }
    return None


def run_tests(channel1: str, channel2: str, bitrate: int):
    print("=" * 60)
    print("  Phase 2: CAN Subsystem Tests")
    print(f"  CAN1: {channel1}, CAN2: {channel2 or 'not connected'}")
    print("=" * 60)

    results = TestResult()
    has_can2 = channel2 is not None

    with PcanBus(channel=channel1, bitrate=bitrate) as bus1:
        # Allow board to settle
        time.sleep(0.5)
        bus1.flush_rx(0.2)

        # ---- T2.1: CAN1 RX single frame ----
        print("\n--- T2.1: CAN1 RX Single Frame ---")
        bus1.send_frame(0x100, [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88])
        echo = bus1.recv_until_id(0x100 + ECHO_OFFSET, timeout=1.0)
        results.check("T2.1", "CAN1 receives frame and echoes",
                       echo is not None,
                       f"echo_id=0x{echo.arbitration_id:03X}" if echo else "no echo")

        # ---- T2.2: CAN1 TX single frame (verify echo data) ----
        print("\n--- T2.2: CAN1 TX Echo Data Integrity ---")
        if echo:
            data_match = list(echo.data[:8]) == [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88]
            results.check("T2.2", "Echo data matches sent data",
                           data_match,
                           f"got={list(echo.data[:8])}")
        else:
            results.check("T2.2", "Echo data matches sent data", False, "no echo received")

        # ---- T2.6: CAN2 disable state ----
        print("\n--- T2.6: CAN2 Starts Disabled ---")
        stats = get_stats(bus1)
        if stats:
            # CAN2 should be in DISABLED state (4) or UNINIT state (0)
            can2_disabled = stats['can2_state'] in (0, 4)
            results.check("T2.6", "CAN2 initially disabled",
                           can2_disabled,
                           f"state={stats['can2_state']}")
        else:
            results.check("T2.6", "CAN2 initially disabled", False, "no stats response")

        # ---- T2.2 (continued): Enable CAN2 ----
        print("\n--- T2.7: CAN2 Enable ---")
        bus1.send_frame(TEST_CMD_ID, [0x02, 0, 0, 0, 0, 0, 0, 0])
        time.sleep(0.3)
        stats = get_stats(bus1)
        if stats:
            can2_active = stats['can2_state'] == 1  # CAN_STATE_ACTIVE
            results.check("T2.7", "CAN2 enables successfully",
                           can2_active,
                           f"state={stats['can2_state']}")
        else:
            results.check("T2.7", "CAN2 enables successfully", False, "no stats response")

        # ---- T2.3 / T2.4: CAN2 TX via loopback command ----
        if has_can2:
            print("\n--- T2.3: CAN2 RX via Second Adapter ---")
            with PcanBus(channel=channel2, bitrate=bitrate) as bus2:
                bus2.flush_rx(0.1)

                # Send frame on CAN2 from host
                bus2.send_frame(0x200, [0xAA, 0xBB, 0xCC, 0xDD])
                echo2 = bus2.recv_until_id(0x200 + ECHO_OFFSET, timeout=1.0)
                results.check("T2.3", "CAN2 receives and echoes frame",
                               echo2 is not None,
                               f"echo_id=0x{echo2.arbitration_id:03X}" if echo2 else "no echo")

                # ---- T2.5: Dual bus simultaneous ----
                print("\n--- T2.5: Dual Bus Simultaneous ---")
                bus1.flush_rx(0.1)
                bus2.flush_rx(0.1)

                rx_count_1 = 0
                rx_count_2 = 0
                for i in range(10):
                    bus1.send_frame(0x100 + i, [i, 0, 0, 0, 0, 0, 0, 0])
                    bus2.send_frame(0x200 + i, [i, 0, 0, 0, 0, 0, 0, 0])

                time.sleep(0.5)

                # Count echoes on both buses
                # CAN1: sent 0x100..0x109 → echoes at 0x200..0x209
                # CAN2: sent 0x200..0x209 → echoes at 0x300..0x309
                deadline = time.time() + 2.0
                while time.time() < deadline:
                    msg = bus1.recv_frame(timeout=0.1)
                    if msg and 0x200 <= msg.arbitration_id <= 0x209:
                        rx_count_1 += 1
                    msg2 = bus2.recv_frame(timeout=0.1)
                    if msg2 and 0x300 <= msg2.arbitration_id <= 0x309:
                        rx_count_2 += 1
                    if rx_count_1 >= 10 and rx_count_2 >= 10:
                        break

                results.check("T2.5", "Both buses echo simultaneously",
                               rx_count_1 >= 8 and rx_count_2 >= 8,
                               f"CAN1_echoes={rx_count_1}, CAN2_echoes={rx_count_2}")
        else:
            print("\n  [SKIP] T2.3, T2.4, T2.5: No second PCAN adapter for CAN2 tests")
            results.check("T2.3", "CAN2 RX (SKIP - no 2nd adapter)", True, "skipped")
            results.check("T2.5", "Dual bus simultaneous (SKIP)", True, "skipped")

        # ---- T2.6 revisited: Disable CAN2 ----
        print("\n--- T2.6: CAN2 Disable at Runtime ---")
        bus1.send_frame(TEST_CMD_ID, [0x03, 0, 0, 0, 0, 0, 0, 0])
        time.sleep(0.3)
        stats = get_stats(bus1)
        if stats:
            disabled = stats['can2_state'] == 4  # CAN_STATE_DISABLED
            results.check("T2.6b", "CAN2 disables at runtime",
                           disabled,
                           f"state={stats['can2_state']}")
        else:
            results.check("T2.6b", "CAN2 disables at runtime", False, "no stats")

        # ---- T2.7: Re-enable CAN2 ----
        print("\n--- T2.7b: CAN2 Re-enable ---")
        bus1.send_frame(TEST_CMD_ID, [0x04, 0, 0, 0, 0, 0, 0, 0])
        time.sleep(0.3)
        stats = get_stats(bus1)
        if stats:
            active = stats['can2_state'] == 1
            results.check("T2.7b", "CAN2 re-enables after disable",
                           active, f"state={stats['can2_state']}")
        else:
            results.check("T2.7b", "CAN2 re-enables after disable", False, "no stats")

        # ---- T2.8: Config ID filtering ----
        print("\n--- T2.8: Config ID Filtering ---")
        bus1.flush_rx(0.1)
        # Send a config frame — it should NOT be echoed (goes to config queue)
        bus1.send_frame(CONFIG_CMD_ID, [0x01, 0, 0, 0, 0, 0, 0, 0])
        echo_cfg = bus1.recv_until_id(CONFIG_CMD_ID + ECHO_OFFSET, timeout=0.5)
        results.check("T2.8", "Config ID 0x600 not echoed (routed to config queue)",
                       echo_cfg is None,
                       "correctly filtered" if echo_cfg is None else "ERROR: was echoed!")

        # ---- T2.9: Non-config ID routed to gateway ----
        print("\n--- T2.9: Non-Config ID Routing ---")
        bus1.flush_rx(0.1)
        bus1.send_frame(0x100, [0xDE, 0xAD])
        echo_gw = bus1.recv_until_id(0x100 + ECHO_OFFSET, timeout=1.0)
        results.check("T2.9", "Non-config ID 0x100 echoed (gateway queue)",
                       echo_gw is not None,
                       "echoed correctly" if echo_gw else "not echoed")

        # ---- T2.10: Stress test ----
        print("\n--- T2.10: Stress Test (100 frames) ---")
        bus1.flush_rx(0.2)
        sent = 100
        for i in range(sent):
            bus1.send_frame(0x100 + (i % 16), [i & 0xFF, 0, 0, 0, 0, 0, 0, 0])

        time.sleep(1.0)
        # Count echoes (sent 0x100..0x10F → echoes at 0x200..0x20F)
        echo_count = 0
        deadline = time.time() + 3.0
        while time.time() < deadline:
            msg = bus1.recv_frame(timeout=0.1)
            if msg and 0x200 <= msg.arbitration_id <= 0x20F:
                echo_count += 1
            if echo_count >= sent:
                break

        loss = sent - echo_count
        results.check("T2.10", f"Stress: {echo_count}/{sent} frames echoed",
                       echo_count >= sent * 0.9,
                       f"loss={loss} ({loss*100//sent}%)")

        # Get final stats
        stats = get_stats(bus1)
        if stats:
            results.check("T2.10b", "No ring buffer overflows during stress",
                           stats['can1_ovf'] == 0,
                           f"overflows={stats['can1_ovf']}")

        # ---- T2.11: Latency estimation ----
        print("\n--- T2.11: RX Latency Estimation ---")
        bus1.flush_rx(0.2)
        t_start = time.time()
        bus1.send_frame(0x100, [0xAA])
        echo_lat = bus1.recv_until_id(0x100 + ECHO_OFFSET, timeout=1.0)
        t_end = time.time()
        if echo_lat:
            latency_ms = (t_end - t_start) * 1000
            results.check("T2.11", f"Round-trip latency ~{latency_ms:.1f} ms",
                           latency_ms < 10.0,
                           f"{latency_ms:.1f} ms (includes host+USB+CAN)")
        else:
            results.check("T2.11", "Latency measurement", False, "no echo")

        # ---- T2.12: Bitrate change ----
        print("\n--- T2.12: Bitrate Change (250 kbps) ---")
        # Tell board to switch to 250 kbps
        bitrate_bytes = struct.pack('<I', 250000)
        bus1.send_frame(TEST_CMD_ID, [0x05] + list(bitrate_bytes) + [0, 0, 0])
        time.sleep(0.5)

    # Re-open at 250 kbps to verify
    try:
        with PcanBus(channel=channel1, bitrate=250000) as bus_250:
            time.sleep(0.3)
            bus_250.flush_rx(0.1)
            bus_250.send_frame(0x100, [0x55])
            echo_250 = bus_250.recv_until_id(0x100 + ECHO_OFFSET, timeout=1.0)
            results.check("T2.12", "Board responds at 250 kbps",
                           echo_250 is not None,
                           "echo received at 250k" if echo_250 else "no response")

            # Switch back to 500 kbps for cleanup
            bitrate_bytes = struct.pack('<I', 500000)
            bus_250.send_frame(TEST_CMD_ID, [0x05] + list(bitrate_bytes) + [0, 0, 0])
    except Exception as e:
        results.check("T2.12", "Board responds at 250 kbps", False, str(e))

    return results.summary()


def main():
    parser = argparse.ArgumentParser(description="Phase 2 CAN Tests")
    parser.add_argument('--channel', default='PCAN_USBBUS1',
                        help='PCAN channel for CAN1 (default: PCAN_USBBUS1)')
    parser.add_argument('--channel2', default=None,
                        help='PCAN channel for CAN2 (optional, for dual-bus tests)')
    parser.add_argument('--bitrate', type=int, default=500000)
    args = parser.parse_args()

    success = run_tests(args.channel, args.channel2, args.bitrate)
    sys.exit(0 if success else 1)


if __name__ == '__main__':
    main()
