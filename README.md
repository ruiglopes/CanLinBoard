# CAN/LIN Gateway Board

Firmware and Windows configuration tool for a custom CAN/LIN gateway board based on the **RP2350 MCU**. The board bridges CAN and LIN buses, acting as a configurable gateway that can pass through, filter, or modify frames in transit.

## Hardware

| Component | Details |
|-----------|---------|
| MCU | RP2350 (ARM Cortex-M33, 150 MHz, FreeRTOS) |
| Flash | 2x 16 Mb — primary (app code) + secondary (NVM config, W25Q128) |
| CAN | 2x transceivers via can2040 (PIO-based). CAN1 always on, CAN2 switchable |
| LIN | 1x SJA1124 SPI transceiver — 4 independent LIN channels |
| CAN Termination | Switchable per bus |
| Boot Button | 1x for bootloader entry |

### Pin Map

| Function | Pin(s) |
|----------|--------|
| CAN1 RX / TX / EN / TERM | 1 / 2 / 3 (active low) / 4 |
| CAN2 RX / TX / EN / TERM | 13 / 14 / 15 (active low) / 12 |
| LIN SPI (spi0) | CS=33, SCK=34, MISO=32, MOSI=23 |
| LIN STAT / INT / CLK | 28 / 26 / 21 (8 MHz from USB PLL) |

## Features

### CAN Gateway
- Bidirectional routing between CAN1 and CAN2
- ID translation, mask-based matching, fan-out routing
- Byte-level mapping with mask, shift, and offset transforms
- Up to 32 configurable routing rules
- CAN2 runtime enable/disable

### LIN Gateway
- 4 independent LIN channels via SJA1124
- Each channel switchable between master and slave mode
- Configurable baud rate (1,000–20,000)
- Master scheduling engine with per-channel schedule tables
- PLL loss-of-lock detection and automatic recovery

### CAN-LIN Bridge
- Cross-protocol frame routing (CAN to LIN and LIN to CAN)
- Byte-level mapping between CAN and LIN frames
- Routed frames to master LIN channels update schedule entry data (no collision with scheduler)

### Diagnostics
- 4-frame periodic heartbeat on configurable CAN ID (default 0x7F0–0x7F4):
  - **Status** (0x7F0): uptime, system state, MCU temperature, reset reason
  - **CAN Stats** (0x7F1): RX counts, error counts, gateway routed count
  - **LIN Stats** (0x7F2): per-channel RX and error counts
  - **System Health** (0x7F4): heap free, min stack watermark, watchdog timeout mask
- Boot-time version frame and crash report (0x7F3)
- Per-bus software watchdogs (CAN1, CAN2, LIN1–4) with configurable timeouts
- Hardware watchdog (5s, fed from FreeRTOS idle hook)
- HardFault handler with crash data persistence across reboot (watchdog scratch registers)

### Configuration
- All settings stored in NVM (secondary flash, ping-pong dual-sector scheme)
- CAN-based config protocol (0x600 command, 0x601 response, 0x602/0x603 bulk data)
- Bulk transfer for routing tables and LIN schedule tables with CRC verification
- Runtime config apply — changes take effect immediately after save
- Bootloader entry via config protocol (with unlock key security)

### Device Profiles (Config Tool)
- Predefined profiles for specific LIN devices (Bosch WDA wiper, Pierburg CWA400 pump)
- Profiles translate device parameters into LIN schedules + routing rules
- DBC/LDF file import for auto-generating profiles
- Custom profile creation and export (JSON format)

## Architecture

### Firmware

```
FreeRTOS Tasks (5 tasks + IRQ handlers)
┌──────────────────────────────────────────────────────────────────┐
│  PIO0 IRQ ──→ [SPSC Ring] ──→ can_task (pri 5) ──→ [gw_queue]  │
│  PIO1 IRQ ──→ [SPSC Ring] ──┘        │                  │      │
│                                  [cfg_queue]        gateway_task │
│                                       │              (pri 3)    │
│                                  config_task      ┌──────┴────┐ │
│                                   (pri 2)     [can_tx]   [lin_tx]│
│  GPIO IRQ ──→ [task notify] ──→ lin_task (pri 4)                │
│                                  diag_task (pri 1)              │
└──────────────────────────────────────────────────────────────────┘
```

- **can_task** — drains CAN RX ring buffers, dispatches to gateway or config queues
- **lin_task** — SJA1124 interrupt handling, LIN RX/TX, master scheduling
- **gateway_task** — routing engine, frame transformation, posts to CAN/LIN TX queues
- **config_task** — CAN config protocol handler, NVM read/write, runtime apply
- **diag_task** — periodic heartbeat, bus watchdogs, system state monitoring

Lock-free SPSC ring buffers for CAN IRQ-to-task communication. PIO IRQ at priority 0 (never masked by FreeRTOS critical sections).

### Flash Memory Layout

```
PRIMARY FLASH (2 MB at 0x10000000):
  0x10000000  Bootloader          (28 KB)
  0x10007000  Bootloader config   (4 KB)
  0x10008000  App header          (256 bytes)
  0x10008100  Application code

SECONDARY FLASH (2 MB via QMI CS1):
  Sector 0    NVM Config Slot A   (4 KB)
  Sector 1    NVM Config Slot B   (4 KB, ping-pong)
  Sector 2    NVM metadata        (4 KB)
  Sector 3+   Reserved
```

### Windows Config Tool

.NET 8 WPF application (MahApps.Metro Dark theme, CommunityToolkit.Mvvm).

**Supported CAN adapters:**

| Adapter | Status | Driver |
|---------|--------|--------|
| PCAN | Full | Peak.PCANBasic.NET (NuGet) |
| Vector XL | Full | vxlapi64.dll |
| SLCAN | Full | System.IO.Ports |
| Kvaser | Untested | canlib32.dll |

**UI:** CAN Config, LIN Config, Gateway Routing, Device Profiles, Diagnostics Settings, Live Diagnostics tabs. Bottom bar with Read All / Write All / Save NVM / Load Defaults / Enter Bootloader.

## Building

### Firmware

```bash
cd firmware
cmake -B build -S . -G Ninja
cmake --build build
```

Requires: Pico SDK 2.1.1, ARM GCC toolchain, Python 3.

The binary is structured for the [2350Bootloader](https://github.com/ruiglopes/2350Bootloader) — flash via the bootloader, not UF2/BOOTSEL.

### Config Tool

```bash
cd software
dotnet build CanLinConfig.sln
```

Requires: .NET 8 SDK, Windows 10/11.

### Running Tests

```bash
# Phase 0 — build verification (no hardware needed)
cd firmware
python tests/phase0/test_build.py

# Phase 1–6 — on-target tests (requires PCAN + board)
cmake --build build --target test_phase1   # or test_phase2..test_phase6
python tests/phase1/test_hal_host.py       # host collector
```

See [firmware/tests/TEST_GUIDE.md](firmware/tests/TEST_GUIDE.md) for full test instructions.

## Repository Structure

```
CanLinBoard/
├── firmware/                  MCU firmware (C, CMake, FreeRTOS)
│   ├── src/                   Source code
│   │   ├── can/               CAN manager (can2040 integration)
│   │   ├── lin/               LIN manager + SJA1124 driver
│   │   ├── gateway/           Routing engine
│   │   ├── config/            Config protocol + NVM
│   │   └── diag/              Diagnostics + watchdogs
│   ├── include/               Headers (board_config.h, app_header.h)
│   ├── config/                FreeRTOSConfig.h, app_config.h
│   ├── lib/                   can2040 (submodule), FreeRTOS-Kernel
│   ├── linker/                Linker scripts
│   ├── tools/                 patch_header.py
│   └── tests/                 Per-phase test firmware + host scripts
├── software/                  Windows config tool (.NET 8, WPF)
│   ├── CanLinConfig/
│   │   ├── Adapters/          CAN adapter implementations
│   │   ├── Protocol/          Config protocol + constants
│   │   ├── Models/            Data models
│   │   ├── Profiles/Devices/  Device profile JSON files
│   │   ├── Services/          Config file I/O
│   │   ├── ViewModels/        MVVM view models
│   │   └── Views/             WPF XAML views
│   └── CanLinConfig.sln
├── docs/                      Implementation plan, roadmap, DBC
├── Datasheets/                Hardware references (SJA1124.pdf)
└── Brief.txt                  Original project specification
```

## Documentation

| Document | Description |
|----------|-------------|
| [docs/implementation-plan.md](docs/implementation-plan.md) | Detailed architecture, phase-by-phase implementation, test plans |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Outstanding work and future features |
| [docs/CHANGELOG.md](docs/CHANGELOG.md) | Version history and completed work |
| [docs/CanLinBoard.dbc](docs/CanLinBoard.dbc) | CAN database — all message and signal definitions |
| [firmware/tests/TEST_GUIDE.md](firmware/tests/TEST_GUIDE.md) | Firmware test guide (Phases 0–6) |
| [software/USER_GUIDE.md](software/USER_GUIDE.md) | Config tool user guide |
| [software/TEST_GUIDE.md](software/TEST_GUIDE.md) | Config tool test guide |

## Key Dependencies

- [can2040](https://github.com/KevinOConnor/can2040) — PIO-based CAN implementation
- [2350Bootloader](https://github.com/ruiglopes/2350Bootloader) — bootloader this firmware targets
- [Pico SDK 2.1.1](https://github.com/raspberrypi/pico-sdk) — RP2350 platform SDK
- [FreeRTOS-Kernel](https://github.com/raspberrypi/FreeRTOS-Kernel) — RPi fork with RP2350_ARM_NTZ port

## CAN Protocol Reference

### Config Protocol

| CAN ID | Direction | Purpose |
|--------|-----------|---------|
| 0x600 | Tool → Board | Config commands |
| 0x601 | Board → Tool | Config responses |
| 0x602 | Tool → Board | Bulk data (7 bytes/frame + seq number) |
| 0x603 | Board → Tool | Bulk read data |

**Commands:** CONNECT (0x01), SAVE (0x02), DEFAULTS (0x03), REBOOT (0x04), ENTER_BL (0x05), GET_STATUS (0x06), READ_PARAM (0x10), WRITE_PARAM (0x11), BULK_START (0x20), BULK_END (0x21), BULK_READ (0x22), BULK_READ_DATA (0x23)

**Config sections:** 0=CAN, 1=LIN, 2=Routing (bulk), 3=Diagnostics, 4=Profiles, 5=Device

### Diagnostics

| CAN ID | Frame | Key Fields |
|--------|-------|------------|
| 0x7F0 | Status | Uptime (32b), SysState, BusMask, MCU Temp, ResetReason |
| 0x7F1 | CAN Stats | CAN1/2 RX counts, error counts, GW routed count |
| 0x7F2 | LIN Stats | LIN1–4 RX and error counts |
| 0x7F3 | Crash Report | Fault type, PC, crash uptime, task name (one-shot) |
| 0x7F4 | System Health | Heap free, min stack watermark, watchdog mask |

### Bootloader

| CAN ID | Purpose |
|--------|---------|
| 0x700 | Bootloader commands (reboot: CMD=0x05, MODE=0x01, key=0xB007CAFE LE) |
| 0x701 | Bootloader response |
| 0x702 | Firmware data during flash |
| 0x7FF | Bootloader debug |

See [docs/CanLinBoard.dbc](docs/CanLinBoard.dbc) for complete signal-level definitions.
