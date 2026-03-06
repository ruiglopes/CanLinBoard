# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Firmware and Windows configuration tool for a custom CAN/LIN gateway board based on the **RP2350 MCU**. The board bridges CAN and LIN buses, acting as a configurable gateway that can pass through, filter, or modify frames in transit. The Windows config tool communicates with the board over CAN to configure all parameters, manage device profiles, and monitor diagnostics.

## Repository Structure

```
CanLinBoard/
├── firmware/              ← MCU project (CMake, RP2350, FreeRTOS)
│   ├── CMakeLists.txt
│   ├── src/               ← firmware source code
│   ├── include/           ← firmware headers
│   ├── config/            ← FreeRTOSConfig.h, app_config.h
│   ├── lib/               ← can2040 (submodule), FreeRTOS-Kernel (cloned)
│   ├── linker/            ← linker scripts
│   ├── tools/             ← patch_header.py and build utilities
│   └── tests/             ← per-phase test firmware and host scripts
│       └── TEST_GUIDE.md  ← firmware test guide (phases 0-6)
├── software/              ← Windows config tool (.NET 8, WPF)
│   ├── CanLinConfig.sln
│   ├── TEST_GUIDE.md      ← config tool test guide
│   └── CanLinConfig/
│       ├── Adapters/      ← CAN adapter implementations (PCAN, Vector XL, Kvaser, SLCAN)
│       ├── Protocol/      ← ConfigProtocol.cs, ProtocolConstants.cs
│       ├── Models/        ← RoutingRule, LinScheduleEntry, ByteMapping
│       ├── Profiles/Devices/  ← WdaWiper.json, Cwa400Pump.json
│       ├── Services/      ← ConfigFileService (JSON export/import)
│       ├── ViewModels/    ← MVVM view models (Main, CAN, LIN, Routing, Profiles, Diagnostics)
│       ├── Views/         ← WPF XAML views
│       └── Helpers/       ← CRC32, converters
├── docs/                  ← implementation-plan.md, CanLinBoard.dbc
├── Datasheets/            ← hardware references (SJA1124.pdf)
├── Brief.txt              ← project specification
└── CLAUDE.md
```

## Hardware

- **MCU:** RP2350
- **Flash:** 2x 16Mb (primary and secondary)
- **CAN:** 2x transceivers (CAN1 always on, CAN2 switchable)
- **LIN:** 1x SJA1124 SPI transceiver (4 LIN channels) — datasheet in `Datasheets/SJA1124.pdf`
- Switchable CAN termination resistors per bus
- 1x Boot button

### Pin Map

| Function       | Pin(s)                        |
|----------------|-------------------------------|
| CAN1 RX/TX/EN/TERM | 1 / 2 / 3 (active low) / 4 |
| CAN2 RX/TX/EN/TERM | 13 / 14 / 15 (active low) / 12 |
| LIN SPI (spi0) | CS=33, SCK=34, MISO=32, MOSI=23 |
| LIN STAT/INT/CLK | 28 / 26 / 21 |

## Key Dependencies

- **can2040** — PIO-based CAN implementation: https://github.com/KevinOConnor/can2040
- **2350Bootloader** — bootloader this firmware targets: https://github.com/ruiglopes/2350Bootloader.git

## Build Commands

```bash
# Build firmware
cd firmware
cmake -B build -S . -G Ninja
cmake --build build

# Run Phase 0 tests (no hardware needed)
python tests/phase0/test_build.py

# Build test firmware for a specific phase
cmake --build build --target test_phase1   # or test_phase2 .. test_phase6

# Build config tool
cd software
dotnet build CanLinConfig.sln
```

## Architecture Constraints

- **No blocking code** — no `sleep()` or busy-wait patterns; use async/event-driven design.
- **All features configurable** and stored in non-volatile memory (NVM).
- **Bootloader-compatible** — binary must be structured for the 2350Bootloader flash layout.

## Functional Requirements

- **CAN gateway:** route/filter/modify frames between CAN1 and CAN2.
- **LIN gateway:** route between LIN buses; each bus switchable between master and slave mode with configurable baud rate. Master mode requires a scheduling table.
- **CAN-LIN bridge:** cross-protocol frame routing with byte-level mapping.
- **Diagnostics:** bus health monitoring, watchdogs, and a configurable CAN diagnostics message.
- **Native device support targets:** Bosch Motorsport WDA LIN wiper motor, Pierburg CWA400 LIN coolant pump.
- **Config tool:** Windows WPF application for device configuration, profile management, and live diagnostics.

## CAN Adapters (Config Tool)

| Adapter | Status | DLL | Notes |
|---------|--------|-----|-------|
| PCAN | Full | PCANBasic.dll (NuGet) | Primary development adapter |
| Vector XL | Full | vxlapi64.dll | Requires Vector XL Driver Library |
| Kvaser | Stub | canlib32.dll | P/Invoke declarations only |
| SLCAN | Full | System.IO.Ports | Serial ASCII protocol (CANable, USBtin) |

## Reference Projects

- DingoPDM firmware (similar concept): https://github.com/corygrant/DingoPDM_FW
- DingoConfig (external config tool): https://github.com/corygrant/dingoConfig
