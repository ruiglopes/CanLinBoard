#!/usr/bin/env python3
"""
Phase 0 Tests: Build Verification & Binary Structure
=====================================================

These tests run entirely on the host (no hardware needed).

Tests:
  T0.1  Clean build compiles with zero errors and zero warnings
  T0.2  App header placed at 0x10008000 (verified via .map file)
  T0.3  Binary header has correct magic, CRC32 patched, size non-zero
  T0.4  patch_header.py produces valid output
  T0.5  Binary structure: vector table at 0x10008100

Prerequisites:
  - PICO_SDK_PATH and FREERTOS_KERNEL_PATH environment variables set
  - ARM GCC toolchain (arm-none-eabi-gcc) in PATH
  - can2040 submodule checked out (git submodule update --init)
  - Python 3 available

Usage:
  cd CanLinBoard/firmware
  python tests/phase0/test_build.py
"""

import os
import sys
import struct
import subprocess
import re
import binascii

# Add common test utilities
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'common'))

PROJECT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
BUILD_DIR = os.path.join(PROJECT_DIR, 'build')
BIN_FILE = os.path.join(BUILD_DIR, 'CanLinBoard.bin')
MAP_FILE = os.path.join(BUILD_DIR, 'CanLinBoard.elf.map')
ELF_FILE = os.path.join(BUILD_DIR, 'CanLinBoard.elf')

APP_HEADER_MAGIC = 0x41505001
APP_BASE = 0x10008000
APP_CODE_BASE = 0x10008100


class Phase0Tests:
    def __init__(self):
        self.passed = 0
        self.failed = 0

    def check(self, test_id, desc, condition, detail=""):
        status = "PASS" if condition else "FAIL"
        msg = f"  [{status}] {test_id}: {desc}"
        if detail:
            msg += f" — {detail}"
        print(msg)
        if condition:
            self.passed += 1
        else:
            self.failed += 1

    def test_t0_1_clean_build(self):
        """T0.1: Clean build with zero errors, zero warnings."""
        print("\n--- T0.1: Clean Build ---")

        # Configure
        os.makedirs(BUILD_DIR, exist_ok=True)
        cmake_cmd = [
            'cmake',
            '-B', BUILD_DIR,
            '-S', PROJECT_DIR,
            '-G', 'Ninja',
        ]

        print(f"  Configuring: {' '.join(cmake_cmd)}")
        result = subprocess.run(cmake_cmd, capture_output=True, text=True, cwd=PROJECT_DIR)

        if result.returncode != 0:
            self.check("T0.1a", "CMake configure", False, f"Exit code {result.returncode}")
            print(f"  STDOUT: {result.stdout[-500:]}")
            print(f"  STDERR: {result.stderr[-500:]}")
            return False

        self.check("T0.1a", "CMake configure succeeds", True)

        # Build
        build_cmd = ['cmake', '--build', BUILD_DIR, '--clean-first']
        print(f"  Building: {' '.join(build_cmd)}")
        result = subprocess.run(build_cmd, capture_output=True, text=True, cwd=PROJECT_DIR)

        build_ok = result.returncode == 0
        self.check("T0.1b", "Build completes without errors", build_ok,
                    f"exit={result.returncode}")

        if not build_ok:
            # Show last 20 lines of output
            lines = (result.stdout + result.stderr).strip().split('\n')
            for line in lines[-20:]:
                print(f"    {line}")
            return False

        # Check for warnings in build output
        combined = result.stdout + result.stderr
        # Filter out informational lines, look for actual warnings
        warning_lines = [l for l in combined.split('\n')
                         if 'warning:' in l.lower() and 'note:' not in l.lower()]
        self.check("T0.1c", "Zero compiler warnings",
                    len(warning_lines) == 0,
                    f"{len(warning_lines)} warnings found" if warning_lines else "")

        for w in warning_lines[:5]:
            print(f"    WARNING: {w.strip()}")

        return build_ok

    def test_t0_2_app_header_placement(self):
        """T0.2: .app_header section at 0x10008000 in map file."""
        print("\n--- T0.2: App Header Placement ---")

        if not os.path.exists(MAP_FILE):
            # Try alternate map file location
            alt_map = os.path.join(BUILD_DIR, 'CanLinBoard.map')
            if os.path.exists(alt_map):
                map_file = alt_map
            else:
                self.check("T0.2", "Map file exists", False,
                           f"Checked {MAP_FILE} and {alt_map}")
                return

        with open(MAP_FILE, 'r', errors='replace') as f:
            map_content = f.read()

        # Look for .app_header section
        header_match = re.search(
            r'\.app_header\s+0x([0-9a-fA-F]+)\s+0x([0-9a-fA-F]+)',
            map_content
        )

        if header_match:
            addr = int(header_match.group(1), 16)
            size = int(header_match.group(2), 16)
            self.check("T0.2a", ".app_header section found in map",
                        True, f"addr=0x{addr:08X}, size=0x{size:X}")
            self.check("T0.2b", ".app_header at 0x10008000",
                        addr == APP_BASE, f"actual=0x{addr:08X}")
            self.check("T0.2c", ".app_header size is 256 bytes",
                        size == 256, f"actual={size}")
        else:
            self.check("T0.2a", ".app_header section found in map", False,
                        "Section not found — check linker script")

    def test_t0_3_binary_header(self):
        """T0.3: Binary header content verification."""
        print("\n--- T0.3: Binary Header Content ---")

        if not os.path.exists(BIN_FILE):
            self.check("T0.3", "Binary file exists", False, BIN_FILE)
            return

        with open(BIN_FILE, 'rb') as f:
            data = f.read()

        self.check("T0.3a", "Binary file non-empty",
                    len(data) > 256, f"size={len(data)} bytes")

        if len(data) < 256:
            return

        # Parse header
        magic, version, size, crc32, entry = struct.unpack_from('<5I', data, 0)

        self.check("T0.3b", f"Magic = 0x{APP_HEADER_MAGIC:08X}",
                    magic == APP_HEADER_MAGIC,
                    f"actual=0x{magic:08X}")

        ver_major = (version >> 16) & 0xFF
        ver_minor = (version >> 8) & 0xFF
        ver_patch = version & 0xFF
        self.check("T0.3c", "Version field is valid",
                    version != 0 and version != 0xFFFFFFFF,
                    f"v{ver_major}.{ver_minor}.{ver_patch}")

        self.check("T0.3d", "Size field non-zero (patched)",
                    size != 0 and size != 0xFFFFFFFF,
                    f"size={size} (0x{size:X})")

        self.check("T0.3e", "CRC32 field non-zero (patched)",
                    crc32 != 0 and crc32 != 0xFFFFFFFF,
                    f"crc32=0x{crc32:08X}")

        self.check("T0.3f", f"Entry point = 0x{APP_CODE_BASE:08X}",
                    entry == APP_CODE_BASE,
                    f"actual=0x{entry:08X}")

        # Verify reserved bytes are 0xFF
        reserved = data[20:256]
        all_ff = all(b == 0xFF for b in reserved)
        self.check("T0.3g", "Reserved bytes are 0xFF",
                    all_ff,
                    f"first non-FF at offset {next((i for i,b in enumerate(reserved) if b != 0xFF), 'none')}")

    def test_t0_4_patch_header_crc(self):
        """T0.4: Verify patch_header.py CRC32 matches actual app data."""
        print("\n--- T0.4: Header CRC Verification ---")

        if not os.path.exists(BIN_FILE):
            self.check("T0.4", "Binary file exists", False)
            return

        with open(BIN_FILE, 'rb') as f:
            data = f.read()

        if len(data) < 257:
            self.check("T0.4", "Binary has app data after header", False)
            return

        magic, version, size_field, crc_field, entry = struct.unpack_from('<5I', data, 0)

        app_data = data[256:]

        # Size should match
        self.check("T0.4a", "Size field matches actual app data length",
                    size_field == len(app_data),
                    f"header says {size_field}, actual {len(app_data)}")

        # Recompute CRC32
        computed_crc = binascii.crc32(app_data) & 0xFFFFFFFF
        self.check("T0.4b", "CRC32 matches computed value",
                    crc_field == computed_crc,
                    f"header=0x{crc_field:08X}, computed=0x{computed_crc:08X}")

    def test_t0_5_vector_table(self):
        """T0.5: Vector table structure at offset 256 (0x10008100)."""
        print("\n--- T0.5: Vector Table Structure ---")

        if not os.path.exists(BIN_FILE):
            self.check("T0.5", "Binary file exists", False)
            return

        with open(BIN_FILE, 'rb') as f:
            data = f.read()

        if len(data) < 256 + 8:
            self.check("T0.5", "Binary has vector table", False)
            return

        # Vector table at offset 256: [initial_sp, reset_handler, ...]
        initial_sp, reset_handler = struct.unpack_from('<II', data, 256)

        # SP should point to top of RAM (0x20000000 + 512K = 0x20080000)
        sp_in_ram = (0x20000000 <= initial_sp <= 0x20082000)
        self.check("T0.5a", "Initial SP points to RAM",
                    sp_in_ram,
                    f"SP=0x{initial_sp:08X}")

        # Reset handler should be in flash (0x10008100+)
        reset_in_flash = (APP_CODE_BASE <= reset_handler <= 0x10400000)
        # Thumb bit: bit 0 should be 1
        thumb_bit = (reset_handler & 1) == 1
        self.check("T0.5b", "Reset handler in flash range",
                    reset_in_flash,
                    f"reset=0x{reset_handler:08X}")
        self.check("T0.5c", "Reset handler has Thumb bit set",
                    thumb_bit,
                    f"bit0={'set' if thumb_bit else 'clear'}")

    def run_all(self):
        print("=" * 60)
        print("  Phase 0: Build Verification & Binary Structure")
        print("=" * 60)

        build_ok = self.test_t0_1_clean_build()
        if build_ok:
            self.test_t0_2_app_header_placement()
            self.test_t0_3_binary_header()
            self.test_t0_4_patch_header_crc()
            self.test_t0_5_vector_table()
        else:
            print("\n  Build failed — skipping binary inspection tests")

        total = self.passed + self.failed
        print(f"\n{'='*60}")
        print(f"  Phase 0 Results: {self.passed}/{total} passed, {self.failed} failed")
        print(f"{'='*60}")
        return self.failed == 0


if __name__ == '__main__':
    tests = Phase0Tests()
    success = tests.run_all()
    sys.exit(0 if success else 1)
