"""
PCAN USB adapter helper for test scripts.

Requires: python-can  (pip install python-can)
          PCAN drivers installed

Usage:
    from pcan_helper import PcanBus

    with PcanBus(channel='PCAN_USBBUS1', bitrate=500000) as bus:
        bus.send_frame(0x100, [0x01, 0x02, 0x03])
        msg = bus.recv_frame(timeout=1.0)
"""

import can
import logging
import time
import struct
from typing import Optional, List

# Suppress noisy bus-error messages from the PCAN driver during board reboots.
# These are expected when the target MCU goes offline temporarily.
logging.getLogger('can').setLevel(logging.CRITICAL)
logging.getLogger('can.pcan').setLevel(logging.CRITICAL)
logging.getLogger('can.interfaces.pcan').setLevel(logging.CRITICAL)


class PcanBus:
    """Wrapper around python-can for PCAN USB adapters."""

    def __init__(self, channel: str = 'PCAN_USBBUS1', bitrate: int = 500000):
        self.channel = channel
        self.bitrate = bitrate
        self.bus: Optional[can.Bus] = None

    def __enter__(self):
        self.open()
        return self

    def __exit__(self, *args):
        self.close()

    def open(self):
        self.bus = can.Bus(
            interface='pcan',
            channel=self.channel,
            bitrate=self.bitrate,
            auto_reset=True,
        )

    def close(self):
        if self.bus:
            self.bus.shutdown()
            self.bus = None

    def reinit(self):
        """Shutdown and recreate the PCAN bus to clear error counters."""
        try:
            if self.bus:
                self.bus.shutdown()
        except Exception:
            pass
        time.sleep(0.3)
        self.open()

    def send_frame(self, can_id: int, data: List[int], extended: bool = False):
        """Send a single CAN frame."""
        msg = can.Message(
            arbitration_id=can_id,
            data=bytes(data),
            is_extended_id=extended,
        )
        self.bus.send(msg)

    def recv_frame(self, timeout: float = 1.0) -> Optional[can.Message]:
        """Receive a single CAN frame (blocking with timeout).
        Silently discards PCAN error frames."""
        deadline = time.time() + timeout
        while time.time() < deadline:
            remaining = max(0.01, deadline - time.time())
            msg = self.bus.recv(timeout=remaining)
            if msg is None:
                return None
            if not msg.is_error_frame:
                return msg
        return None

    def recv_frames(self, count: int, timeout: float = 5.0) -> List[can.Message]:
        """Receive up to `count` frames within `timeout` seconds.
        Silently discards PCAN error frames."""
        frames = []
        deadline = time.time() + timeout
        while len(frames) < count and time.time() < deadline:
            remaining = deadline - time.time()
            msg = self.bus.recv(timeout=max(0.01, remaining))
            if msg and not msg.is_error_frame:
                frames.append(msg)
        return frames

    def recv_until_id(self, target_id: int, timeout: float = 2.0) -> Optional[can.Message]:
        """Receive frames until one matches `target_id`.
        Silently discards PCAN error frames."""
        deadline = time.time() + timeout
        while time.time() < deadline:
            msg = self.bus.recv(timeout=max(0.01, deadline - time.time()))
            if not msg or msg.is_error_frame:
                continue
            if msg.arbitration_id == target_id:
                return msg
        return None

    def flush_rx(self, duration: float = 0.1):
        """Drain any pending RX frames."""
        deadline = time.time() + duration
        while time.time() < deadline:
            self.bus.recv(timeout=0.01)

    def send_config_cmd(self, cmd: int, data: List[int] = None) -> Optional[can.Message]:
        """Send a config command on 0x600 and wait for response on 0x601."""
        payload = [cmd] + (data or [])
        payload += [0] * (8 - len(payload))
        self.flush_rx()
        self.send_frame(0x600, payload[:8])
        return self.recv_until_id(0x601, timeout=0.5)


class TestResult:
    """Simple test result tracking."""

    def __init__(self):
        self.passed = 0
        self.failed = 0
        self.results = []

    def check(self, test_id: str, description: str, condition: bool, detail: str = ""):
        status = "PASS" if condition else "FAIL"
        entry = f"  [{status}] {test_id}: {description}"
        if detail:
            entry += f" — {detail}"
        self.results.append(entry)
        if condition:
            self.passed += 1
        else:
            self.failed += 1
        print(entry)

    def summary(self):
        total = self.passed + self.failed
        print(f"\n{'='*60}")
        print(f"  Results: {self.passed}/{total} passed, {self.failed} failed")
        print(f"{'='*60}")
        return self.failed == 0
