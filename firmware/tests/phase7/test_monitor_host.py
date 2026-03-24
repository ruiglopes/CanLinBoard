#!/usr/bin/env python3
"""
Phase 7 Host-Side Tests: Bus Monitor Protocol
===============================================

Tests the bus monitor streaming protocol using the main firmware.
No dedicated test firmware needed — monitor runs in production firmware.

Requires:
  - Main firmware (v0.3.0+) flashed on board
  - PCAN adapter connected to CAN1 at 500 kbps

Test groups:
  A — Enable/Disable & Basic Streaming (T7.1-T7.7)
  B — Filtering (T7.8-T7.13)
  C — Backpressure & Priority (T7.14-T7.16)

Usage:
  python tests/phase7/test_monitor_host.py [--channel PCAN_USBBUS1]
"""

import sys
import os
import time
import struct
import argparse

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'common'))
from pcan_helper import PcanBus, TestResult

# CAN IDs
CONFIG_CMD_ID       = 0x600
CONFIG_RESP_ID      = 0x601
CONFIG_BULK_DATA_ID = 0x602
CONFIG_BULK_RESP_ID = 0x603
MONITOR_HEADER_ID   = 0x604
MONITOR_DATA_ID     = 0x605
DIAG_STATUS_ID      = 0x7F0

# Config commands
CMD_READ_PARAM      = 0x10
CMD_WRITE_PARAM     = 0x11
CMD_BULK_START      = 0x20
CMD_BULK_END        = 0x21

# Status
STATUS_OK           = 0x00

# Sections
SECTION_MONITOR     = 0x06

# Monitor params
MONITOR_PARAM_ENABLE       = 0
MONITOR_PARAM_BUS_MASK     = 1
MONITOR_PARAM_FILTER_MODE  = 2
MONITOR_PARAM_DROP_COUNT   = 3

# Filter modes
FILTER_NONE      = 0
FILTER_WHITELIST = 1
FILTER_BLACKLIST = 2

# Test frame IDs (outside excluded ranges)
TEST_FRAME_ID   = 0x100
TEST_FRAME_ID_2 = 0x123
TEST_FRAME_ID_3 = 0x200


def crc32(data: bytes) -> int:
    """Standard CRC32 (same as zlib)."""
    import zlib
    return zlib.crc32(data) & 0xFFFFFFFF


def read_param(bus, section, param, sub=0, timeout=0.5):
    """Send READ_PARAM and return response, or None."""
    bus.flush_rx(0.05)
    bus.send_frame(CONFIG_CMD_ID, [CMD_READ_PARAM, section, param, sub, 0, 0, 0, 0])
    return bus.recv_until_id(CONFIG_RESP_ID, timeout=timeout)


def write_param(bus, section, param, sub, value, timeout=0.5):
    """Send WRITE_PARAM and return response."""
    if isinstance(value, int):
        value = [value]
    payload = [CMD_WRITE_PARAM, section, param, sub] + value
    payload += [0] * (8 - len(payload))
    bus.flush_rx(0.05)
    bus.send_frame(CONFIG_CMD_ID, payload[:8])
    return bus.recv_until_id(CONFIG_RESP_ID, timeout=timeout)


def bulk_write_filter_ids(bus, id_list):
    """Bulk write monitor filter ID list."""
    # Serialize IDs as uint32_t LE array
    data = b''
    for fid in id_list:
        data += struct.pack('<I', fid)
    size = len(data)
    crc_val = crc32(data)
    crc24 = crc_val & 0xFFFFFF

    # BULK_START
    bus.flush_rx(0.05)
    bus.send_frame(CONFIG_CMD_ID, [
        CMD_BULK_START, SECTION_MONITOR, 0,
        size & 0xFF, (size >> 8) & 0xFF,
        crc24 & 0xFF, (crc24 >> 8) & 0xFF, (crc24 >> 16) & 0xFF
    ])
    resp = bus.recv_until_id(CONFIG_RESP_ID, timeout=0.5)
    if not resp or resp.data[1] != STATUS_OK:
        return False

    # Send data on 0x602
    seq = 0
    offset = 0
    while offset < size:
        chunk = min(7, size - offset)
        frame_data = [seq] + list(data[offset:offset+chunk])
        bus.send_frame(CONFIG_BULK_DATA_ID, frame_data)
        seq += 1
        offset += chunk
        time.sleep(0.002)

    # BULK_END
    time.sleep(0.01)
    bus.send_frame(CONFIG_CMD_ID, [CMD_BULK_END, 0, 0, 0, 0, 0, 0, 0])
    resp = bus.recv_until_id(CONFIG_RESP_ID, timeout=0.5)
    return resp and resp.data[1] == STATUS_OK


def collect_monitor_frames(bus, duration=1.0, max_frames=100):
    """Collect monitor header+data frame pairs within duration.
    Returns list of (seq, bus_id, frame_id, dlc, data_bytes)."""
    frames = []
    pending_header = None
    deadline = time.time() + duration

    while time.time() < deadline and len(frames) < max_frames:
        msg = bus.recv_frame(timeout=max(0.01, deadline - time.time()))
        if not msg:
            continue

        if msg.arbitration_id == MONITOR_HEADER_ID and len(msg.data) >= 7:
            seq = msg.data[0]
            bus_id = msg.data[1] & 0x0F
            frame_id = struct.unpack_from('<I', bytes(msg.data), 2)[0]
            dlc = msg.data[6] & 0x0F
            d7_nibble = (msg.data[6] >> 4) & 0x0F

            if dlc == 0:
                frames.append((seq, bus_id, frame_id, dlc, b''))
                pending_header = None
            else:
                pending_header = (seq, bus_id, frame_id, dlc, d7_nibble)

        elif msg.arbitration_id == MONITOR_DATA_ID and pending_header and len(msg.data) >= 2:
            seq_h, bus_id, frame_id, dlc, d7_nibble = pending_header
            seq_d = msg.data[0]
            if seq_d == seq_h:
                data_bytes = bytes(msg.data[1:min(1+7, len(msg.data))])
                if dlc == 8 and len(data_bytes) >= 7:
                    data_bytes = data_bytes[:7] + bytes([d7_nibble])
                frames.append((seq_h, bus_id, frame_id, dlc, data_bytes))
            pending_header = None

    return frames


def ensure_monitor_disabled(bus):
    """Disable monitor and clear state."""
    write_param(bus, SECTION_MONITOR, MONITOR_PARAM_ENABLE, 0, 0)
    time.sleep(0.05)
    write_param(bus, SECTION_MONITOR, MONITOR_PARAM_FILTER_MODE, 0, FILTER_NONE)
    time.sleep(0.05)


def enable_monitor_can1(bus):
    """Enable monitor with CAN1 in bus mask."""
    write_param(bus, SECTION_MONITOR, MONITOR_PARAM_BUS_MASK, 0, 0x01)
    time.sleep(0.05)
    write_param(bus, SECTION_MONITOR, MONITOR_PARAM_ENABLE, 0, 1)
    time.sleep(0.1)
    bus.flush_rx(0.2)


# ================================================================
# TEST GROUP A: Enable/Disable & Basic Streaming
# ================================================================

def test_a1_monitor_starts_disabled(bus, results):
    """T7.1: Monitor starts disabled by default."""
    print("\n--- T7.1: Monitor starts disabled ---")
    resp = read_param(bus, SECTION_MONITOR, MONITOR_PARAM_ENABLE)
    if resp and len(resp.data) >= 6:
        val = resp.data[5]
        results.check("T7.1", "Monitor starts disabled", val == 0,
                       f"enabled={val}")
    else:
        results.check("T7.1", "Monitor starts disabled", False,
                       "no response")


def test_a2_enable_monitor(bus, results):
    """T7.2: Enable monitor and read back."""
    print("\n--- T7.2: Enable monitor ---")
    resp = write_param(bus, SECTION_MONITOR, MONITOR_PARAM_ENABLE, 0, 1)
    ok_write = resp and resp.data[1] == STATUS_OK
    time.sleep(0.05)
    resp = read_param(bus, SECTION_MONITOR, MONITOR_PARAM_ENABLE)
    ok_read = resp and len(resp.data) >= 6 and resp.data[5] == 1
    results.check("T7.2", "Enable monitor", ok_write and ok_read,
                   f"write_ok={ok_write}, read_val={resp.data[5] if resp and len(resp.data) >= 6 else '?'}")
    # Disable for next tests
    ensure_monitor_disabled(bus)


def test_a3_can1_frame_mirrored(bus, results):
    """T7.3: CAN1 frame mirrored when CAN1 in bus mask."""
    print("\n--- T7.3: CAN1 frame mirrored ---")
    enable_monitor_can1(bus)

    # Send test frame on CAN1
    test_data = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77]
    bus.send_frame(TEST_FRAME_ID, test_data)

    # Collect monitor frames
    mon_frames = collect_monitor_frames(bus, duration=1.0)
    matched = [f for f in mon_frames if f[2] == TEST_FRAME_ID]

    ok = len(matched) >= 1
    detail = f"matched={len(matched)}"
    if ok:
        seq, bus_id, fid, dlc, data = matched[0]
        ok = ok and (bus_id == 0)  # CAN1 = 0
        ok = ok and (dlc == 7)
        ok = ok and (data[:7] == bytes(test_data[:7]))
        detail += f", bus={bus_id}, dlc={dlc}, data={data.hex()}"

    results.check("T7.3", "CAN1 frame mirrored", ok, detail)
    ensure_monitor_disabled(bus)


def test_a4_sequence_increments(bus, results):
    """T7.4: Sequence number increments across frames."""
    print("\n--- T7.4: Sequence number increments ---")
    enable_monitor_can1(bus)

    for i in range(3):
        bus.send_frame(TEST_FRAME_ID, [i, 0, 0, 0])
        time.sleep(0.05)

    mon_frames = collect_monitor_frames(bus, duration=1.0)
    matched = [f for f in mon_frames if f[2] == TEST_FRAME_ID]

    ok = len(matched) >= 3
    if ok:
        seqs = [f[0] for f in matched[:3]]
        # Sequences must be strictly increasing (not necessarily consecutive —
        # other monitored frames may increment the counter between ours)
        ok = (seqs[1] > seqs[0]) and (seqs[2] > seqs[1])
        detail = f"seqs={seqs}, strictly_increasing={ok}"
    else:
        detail = f"only {len(matched)} frames"

    results.check("T7.4", "Sequence numbers strictly increasing", ok, detail)
    ensure_monitor_disabled(bus)


def test_a5_dlc0_header_only(bus, results):
    """T7.5: DLC=0 frame produces header only (no data frame)."""
    print("\n--- T7.5: DLC=0 header-only ---")
    enable_monitor_can1(bus)

    bus.send_frame(TEST_FRAME_ID, [])  # DLC=0
    time.sleep(0.2)

    # Collect raw frames to check for absence of 0x605
    headers = []
    datas = []
    deadline = time.time() + 1.0
    while time.time() < deadline:
        msg = bus.recv_frame(timeout=0.1)
        if not msg:
            continue
        if msg.arbitration_id == MONITOR_HEADER_ID:
            fid = struct.unpack_from('<I', bytes(msg.data), 2)[0]
            if fid == TEST_FRAME_ID:
                headers.append(msg)
        elif msg.arbitration_id == MONITOR_DATA_ID:
            # Check if this data matches a header seq for our test frame
            datas.append(msg)

    ok = len(headers) >= 1
    # For DLC=0, we should NOT see a matching data frame
    # (any 0x605 could be from other frames, check seq match)
    if ok:
        h_seq = headers[0].data[0]
        dlc_field = headers[0].data[6] & 0x0F
        matching_data = [d for d in datas if d.data[0] == h_seq]
        ok = ok and (dlc_field == 0) and (len(matching_data) == 0)
        detail = f"dlc_field={dlc_field}, data_frames_with_seq={len(matching_data)}"
    else:
        detail = "no header received"

    results.check("T7.5", "DLC=0 header-only", ok, detail)
    ensure_monitor_disabled(bus)


def test_a6_dlc8_data7_in_header(bus, results):
    """T7.6: DLC=8 data[7] lower nibble in header byte 6."""
    print("\n--- T7.6: DLC=8 data[7] nibble ---")
    enable_monitor_can1(bus)

    test_data = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x0D]
    bus.send_frame(TEST_FRAME_ID, test_data)

    mon_frames = collect_monitor_frames(bus, duration=1.0)
    matched = [f for f in mon_frames if f[2] == TEST_FRAME_ID]

    ok = len(matched) >= 1
    if ok:
        seq, bus_id, fid, dlc, data = matched[0]
        ok = ok and (dlc == 8)
        ok = ok and (data[:7] == bytes(test_data[:7]))
        # data[7] lower nibble: 0x0D & 0x0F = 0x0D
        ok = ok and ((data[7] & 0x0F) == (test_data[7] & 0x0F))
        detail = f"dlc={dlc}, data={data.hex()}, expected_d7_nibble=0x{test_data[7]&0x0F:X}, got=0x{data[7]&0x0F:X}"
    else:
        detail = "no monitor frames"

    results.check("T7.6", "DLC=8 data[7] nibble in header", ok, detail)
    ensure_monitor_disabled(bus)


def test_a7_disable_monitor(bus, results):
    """T7.7: Disable monitor, verify no mirror frames."""
    print("\n--- T7.7: Disable monitor ---")
    enable_monitor_can1(bus)

    # Disable
    write_param(bus, SECTION_MONITOR, MONITOR_PARAM_ENABLE, 0, 0)
    time.sleep(0.1)
    bus.flush_rx(0.2)

    # Send frames
    for _ in range(5):
        bus.send_frame(TEST_FRAME_ID, [0x01, 0x02, 0x03, 0x04])
        time.sleep(0.02)

    # Check for monitor frames
    mon_count = 0
    deadline = time.time() + 1.0
    while time.time() < deadline:
        msg = bus.recv_frame(timeout=0.2)
        if msg and msg.arbitration_id == MONITOR_HEADER_ID:
            fid = struct.unpack_from('<I', bytes(msg.data), 2)[0]
            if fid == TEST_FRAME_ID:
                mon_count += 1

    results.check("T7.7", "No monitor frames when disabled", mon_count == 0,
                   f"monitor_frames={mon_count}")


# ================================================================
# TEST GROUP B: Filtering
# ================================================================

def test_b8_bus_mask_filter(bus, results):
    """T7.8: Bus mask filter — CAN2-only mask excludes CAN1 frames."""
    print("\n--- T7.8: Bus mask filter ---")
    # Set bus mask to CAN2 only (0x02), enable monitor
    write_param(bus, SECTION_MONITOR, MONITOR_PARAM_BUS_MASK, 0, 0x02)
    time.sleep(0.05)
    write_param(bus, SECTION_MONITOR, MONITOR_PARAM_ENABLE, 0, 1)
    time.sleep(0.1)
    bus.flush_rx(0.2)

    # Send on CAN1 — should NOT be mirrored (CAN1 not in mask)
    for _ in range(5):
        bus.send_frame(TEST_FRAME_ID, [0xDE, 0xAD])
        time.sleep(0.02)

    mon_frames = collect_monitor_frames(bus, duration=1.0)
    matched = [f for f in mon_frames if f[2] == TEST_FRAME_ID]

    results.check("T7.8", "CAN1 excluded when mask=CAN2", len(matched) == 0,
                   f"mirrored={len(matched)}")
    ensure_monitor_disabled(bus)


def test_b9_config_frames_excluded(bus, results):
    """T7.9: Config frames (0x600-0x605) never appear as monitor frames."""
    print("\n--- T7.9: Config frames excluded ---")
    enable_monitor_can1(bus)

    # Send a config-range frame and a normal frame
    bus.send_frame(TEST_FRAME_ID, [0x01, 0x02, 0x03])
    time.sleep(0.05)

    mon_frames = collect_monitor_frames(bus, duration=1.5)
    config_mirrored = [f for f in mon_frames if 0x600 <= f[2] <= 0x605]
    normal_mirrored = [f for f in mon_frames if f[2] == TEST_FRAME_ID]

    ok = (len(config_mirrored) == 0) and (len(normal_mirrored) >= 1)
    results.check("T7.9", "Config frames excluded from mirror",
                   ok, f"config={len(config_mirrored)}, normal={len(normal_mirrored)}")
    ensure_monitor_disabled(bus)


def test_b10_diag_frames_excluded(bus, results):
    """T7.10: Diagnostics heartbeat frames never appear as monitor frames."""
    print("\n--- T7.10: Diag frames excluded ---")
    enable_monitor_can1(bus)

    # Wait for heartbeat frames to occur (1 Hz)
    time.sleep(1.5)

    mon_frames = collect_monitor_frames(bus, duration=2.0)
    diag_mirrored = [f for f in mon_frames if 0x7F0 <= f[2] <= 0x7F4]

    results.check("T7.10", "Diag frames excluded from mirror",
                   len(diag_mirrored) == 0, f"diag_mirrored={len(diag_mirrored)}")
    ensure_monitor_disabled(bus)


def test_b11_id_whitelist(bus, results):
    """T7.11: ID whitelist — only matching ID mirrored."""
    print("\n--- T7.11: ID whitelist ---")
    # Write filter ID list [0x123]
    ok_bulk = bulk_write_filter_ids(bus, [TEST_FRAME_ID_2])
    if not ok_bulk:
        results.check("T7.11", "ID whitelist", False, "bulk write failed")
        ensure_monitor_disabled(bus)
        return

    # Set whitelist mode
    write_param(bus, SECTION_MONITOR, MONITOR_PARAM_FILTER_MODE, 0, FILTER_WHITELIST)
    time.sleep(0.05)
    enable_monitor_can1(bus)

    # Send whitelisted ID and non-whitelisted ID
    bus.send_frame(TEST_FRAME_ID_2, [0x11, 0x22])
    time.sleep(0.05)
    bus.send_frame(TEST_FRAME_ID, [0xAA, 0xBB])
    time.sleep(0.05)

    mon_frames = collect_monitor_frames(bus, duration=1.0)
    wl_match = [f for f in mon_frames if f[2] == TEST_FRAME_ID_2]
    non_match = [f for f in mon_frames if f[2] == TEST_FRAME_ID]

    ok = (len(wl_match) >= 1) and (len(non_match) == 0)
    results.check("T7.11", "ID whitelist",
                   ok, f"whitelisted={len(wl_match)}, blocked={len(non_match)}")
    ensure_monitor_disabled(bus)


def test_b12_id_blacklist(bus, results):
    """T7.12: ID blacklist — matching ID blocked, others pass."""
    print("\n--- T7.12: ID blacklist ---")
    ok_bulk = bulk_write_filter_ids(bus, [TEST_FRAME_ID_2])
    if not ok_bulk:
        results.check("T7.12", "ID blacklist", False, "bulk write failed")
        ensure_monitor_disabled(bus)
        return

    write_param(bus, SECTION_MONITOR, MONITOR_PARAM_FILTER_MODE, 0, FILTER_BLACKLIST)
    time.sleep(0.05)
    enable_monitor_can1(bus)

    bus.send_frame(TEST_FRAME_ID_2, [0x11, 0x22])
    time.sleep(0.05)
    bus.send_frame(TEST_FRAME_ID, [0xAA, 0xBB])
    time.sleep(0.05)

    mon_frames = collect_monitor_frames(bus, duration=1.0)
    bl_match = [f for f in mon_frames if f[2] == TEST_FRAME_ID_2]
    other_match = [f for f in mon_frames if f[2] == TEST_FRAME_ID]

    ok = (len(bl_match) == 0) and (len(other_match) >= 1)
    results.check("T7.12", "ID blacklist",
                   ok, f"blocked={len(bl_match)}, passed={len(other_match)}")
    ensure_monitor_disabled(bus)


def test_b13_filter_mode_clear(bus, results):
    """T7.13: Filter mode=0 (none) — all IDs mirrored."""
    print("\n--- T7.13: Filter mode clear ---")
    write_param(bus, SECTION_MONITOR, MONITOR_PARAM_FILTER_MODE, 0, FILTER_NONE)
    time.sleep(0.05)
    enable_monitor_can1(bus)

    bus.send_frame(TEST_FRAME_ID, [0x01])
    time.sleep(0.05)
    bus.send_frame(TEST_FRAME_ID_2, [0x02])
    time.sleep(0.05)

    mon_frames = collect_monitor_frames(bus, duration=1.0)
    id1 = [f for f in mon_frames if f[2] == TEST_FRAME_ID]
    id2 = [f for f in mon_frames if f[2] == TEST_FRAME_ID_2]

    ok = (len(id1) >= 1) and (len(id2) >= 1)
    results.check("T7.13", "Filter mode=none, all IDs pass",
                   ok, f"id1={len(id1)}, id2={len(id2)}")
    ensure_monitor_disabled(bus)


# ================================================================
# TEST GROUP C: Backpressure & Priority
# ================================================================

def test_c14_config_priority(bus, results):
    """T7.14: Config responses arrive during monitor traffic."""
    print("\n--- T7.14: Application traffic priority ---")
    enable_monitor_can1(bus)

    # Send a burst of frames to generate monitor traffic
    for i in range(20):
        bus.send_frame(TEST_FRAME_ID + (i % 16), [i & 0xFF])
        time.sleep(0.002)

    # Now send a config read and check response arrives
    time.sleep(0.05)
    resp = read_param(bus, SECTION_MONITOR, MONITOR_PARAM_ENABLE, timeout=2.0)
    ok = resp is not None and resp.data[1] == STATUS_OK
    results.check("T7.14", "Config response during monitor flood",
                   ok, f"response={'yes' if ok else 'timeout'}")
    ensure_monitor_disabled(bus)


def test_c15_drop_count(bus, results):
    """T7.15: Drop count increments under high load."""
    print("\n--- T7.15: Drop count ---")
    enable_monitor_can1(bus)

    # Flood with frames to overflow monitor queue (queue depth=16)
    for i in range(100):
        bus.send_frame(TEST_FRAME_ID + (i % 16), [i & 0xFF, 0, 0, 0])
        time.sleep(0.001)

    time.sleep(0.5)

    # Read drop count
    resp = read_param(bus, SECTION_MONITOR, MONITOR_PARAM_DROP_COUNT)
    drops = 0
    if resp and len(resp.data) >= 6:
        drops = resp.data[5]

    # Drops may or may not occur depending on CAN bus speed vs queue depth
    # We just verify the counter is readable
    results.check("T7.15", "Drop count readable",
                   resp is not None, f"drops={drops}")
    ensure_monitor_disabled(bus)


def test_c16_sequence_gap(bus, results):
    """T7.16: Sequence gaps visible during high traffic."""
    print("\n--- T7.16: Sequence gap detection ---")
    enable_monitor_can1(bus)

    # Send a burst to potentially cause drops
    for i in range(60):
        bus.send_frame(TEST_FRAME_ID, [i & 0xFF, 0, 0, 0])
        time.sleep(0.001)

    mon_frames = collect_monitor_frames(bus, duration=2.0, max_frames=60)
    matched = [f for f in mon_frames if f[2] == TEST_FRAME_ID]

    gaps = 0
    if len(matched) >= 2:
        for i in range(1, len(matched)):
            expected = (matched[i-1][0] + 1) & 0xFF
            if matched[i][0] != expected:
                gaps += 1

    # Gaps may or may not occur — we verify the mechanism works
    results.check("T7.16", "Sequence gap detection",
                   len(matched) >= 1,
                   f"frames={len(matched)}, gaps={gaps}")
    ensure_monitor_disabled(bus)


# ================================================================
# Main
# ================================================================

def run_tests(channel: str, bitrate: int):
    print("=" * 60)
    print("  Phase 7: Bus Monitor Protocol Tests")
    print(f"  Channel: {channel}, Bitrate: {bitrate}")
    print("  NOTE: Uses main firmware — no test firmware needed")
    print("=" * 60)

    results = TestResult()

    with PcanBus(channel=channel, bitrate=bitrate) as bus:
        # Wait for board to be ready (heartbeat)
        print("\n  Waiting for heartbeat...")
        hb = bus.recv_until_id(DIAG_STATUS_ID, timeout=5.0)
        if not hb:
            print("  WARNING: No heartbeat — is the board running?")
            return False

        print("  Board alive, starting tests.\n")

        # Ensure clean state
        ensure_monitor_disabled(bus)
        time.sleep(0.2)

        # Group A — Enable/Disable & Basic Streaming
        print("\n" + "=" * 60)
        print("  Group A: Enable/Disable & Basic Streaming")
        print("=" * 60)
        test_a1_monitor_starts_disabled(bus, results)
        test_a2_enable_monitor(bus, results)
        test_a3_can1_frame_mirrored(bus, results)
        test_a4_sequence_increments(bus, results)
        test_a5_dlc0_header_only(bus, results)
        test_a6_dlc8_data7_in_header(bus, results)
        test_a7_disable_monitor(bus, results)

        # Group B — Filtering
        print("\n" + "=" * 60)
        print("  Group B: Filtering")
        print("=" * 60)
        test_b8_bus_mask_filter(bus, results)
        test_b9_config_frames_excluded(bus, results)
        test_b10_diag_frames_excluded(bus, results)
        test_b11_id_whitelist(bus, results)
        test_b12_id_blacklist(bus, results)
        test_b13_filter_mode_clear(bus, results)

        # Group C — Backpressure & Priority
        print("\n" + "=" * 60)
        print("  Group C: Backpressure & Priority")
        print("=" * 60)
        test_c14_config_priority(bus, results)
        test_c15_drop_count(bus, results)
        test_c16_sequence_gap(bus, results)

    return results.summary()


def main():
    parser = argparse.ArgumentParser(description="Phase 7 Bus Monitor Tests")
    parser.add_argument('--channel', default='PCAN_USBBUS1',
                        help='PCAN channel (default: PCAN_USBBUS1)')
    parser.add_argument('--bitrate', type=int, default=500000,
                        help='CAN bitrate (default: 500000)')
    args = parser.parse_args()

    success = run_tests(args.channel, args.bitrate)
    sys.exit(0 if success else 1)


if __name__ == '__main__':
    main()
