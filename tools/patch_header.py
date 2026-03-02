#!/usr/bin/env python3
"""
Post-build script: patches the application binary header with correct
size, CRC32, and entry_point fields.

The binary layout:
  Offset 0x00: app_header_t (256 bytes)
  Offset 0x100+: application code and data

This script:
  1. Reads the .bin file
  2. Computes CRC32 of the application data (bytes 256 onwards)
  3. Patches header fields: size, crc32, entry_point
  4. Writes the patched binary back

Usage:
  python patch_header.py <input.bin> [output.bin]
  If output is omitted, patches in-place.
"""

import sys
import struct
import binascii

APP_HEADER_SIZE = 256
APP_HEADER_MAGIC = 0x41505001
APP_CODE_BASE = 0x10008100  # Where VTOR lives

# Header struct layout (little-endian):
#   uint32_t magic;       offset 0
#   uint32_t version;     offset 4
#   uint32_t size;        offset 8
#   uint32_t crc32;       offset 12
#   uint32_t entry_point; offset 16
#   uint8_t  reserved[236];

HEADER_FMT = '<5I'  # 5x uint32_t = 20 bytes


def patch_header(input_path, output_path=None):
    if output_path is None:
        output_path = input_path

    with open(input_path, 'rb') as f:
        data = bytearray(f.read())

    if len(data) < APP_HEADER_SIZE:
        print(f"ERROR: Binary too small ({len(data)} bytes, need >= {APP_HEADER_SIZE})")
        sys.exit(1)

    # Read current header
    magic, version, size_field, crc_field, entry_field = struct.unpack_from(HEADER_FMT, data, 0)

    if magic != APP_HEADER_MAGIC:
        print(f"ERROR: Invalid magic 0x{magic:08X}, expected 0x{APP_HEADER_MAGIC:08X}")
        sys.exit(1)

    # Application data starts after header
    app_data = data[APP_HEADER_SIZE:]
    app_size = len(app_data)

    if app_size == 0:
        print("ERROR: No application data after header")
        sys.exit(1)

    # Compute CRC32 (standard, same as bootloader's crc32())
    crc = binascii.crc32(app_data) & 0xFFFFFFFF

    # Patch header fields
    struct.pack_into('<I', data, 8, app_size)       # size
    struct.pack_into('<I', data, 12, crc)            # crc32
    struct.pack_into('<I', data, 16, APP_CODE_BASE)  # entry_point

    with open(output_path, 'wb') as f:
        f.write(data)

    print(f"Patched: size={app_size} (0x{app_size:X}), "
          f"CRC32=0x{crc:08X}, entry=0x{APP_CODE_BASE:08X}")
    print(f"Version: {(version>>16)&0xFF}.{(version>>8)&0xFF}.{version&0xFF}")
    print(f"Output: {output_path}")


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(f"Usage: {sys.argv[0]} <input.bin> [output.bin]")
        sys.exit(1)

    in_path = sys.argv[1]
    out_path = sys.argv[2] if len(sys.argv) > 2 else None
    patch_header(in_path, out_path)
