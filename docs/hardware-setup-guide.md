# Hardware Setup Guide

This guide covers wiring, connections, and configuration for the CAN/LIN gateway board.

---

## Board Overview

| Component | Detail |
|-----------|--------|
| MCU | RP2350 (QFN-80), 150 MHz, 520 KB SRAM |
| Flash | 2x 16 Mb — primary (program) + secondary (NVM + data logging) |
| CAN | 2x transceivers (CAN1 always on, CAN2 switchable) |
| LIN | 1x SJA1124 SPI transceiver (4 LIN channels) |
| CAN Termination | 2x switchable 120 ohm resistors (one per bus) |
| Button | 1x boot button (bootloader entry) |
| Bootloader | [2350Bootloader](https://github.com/ruiglopes/2350Bootloader) — firmware flashed via CAN |
| Connector | Molex MX120G, 12-pin |

---

## Connector Pinout (Molex MX120G)

```
        ┌─────────────────────────┐
        │  1   2   3   4   5   6  │
        │  7   8   9  10  11  12  │
        └─────────────────────────┘
```

| Pin | Signal | Description |
|-----|--------|-------------|
| 1 | VBATT | Battery voltage supply (8-18V typical, 12V nominal) |
| 2 | LIN2 | LIN bus channel 2 |
| 3 | BOOT | Bootloader button (ground to enter bootloader on power-up) |
| 4 | LIN4 | LIN bus channel 4 |
| 5 | CAN2_L | CAN2 Low |
| 6 | CAN1_L | CAN1 Low |
| 7 | GND | Ground |
| 8 | LIN1 | LIN bus channel 1 |
| 9 | GND | Ground |
| 10 | LIN3 | LIN bus channel 3 |
| 11 | CAN2_H | CAN2 High |
| 12 | CAN1_H | CAN1 High |

### Wiring Summary

| Bus | Pins | Notes |
|-----|------|-------|
| CAN1 | 12 (H), 6 (L), 7 or 9 (GND) | Always active, config tool + bootloader |
| CAN2 | 11 (H), 5 (L), 7 or 9 (GND) | Must be enabled via config |
| LIN1 | 8, 7 or 9 (GND) | + VBATT (pin 1) for LIN pull-up |
| LIN2 | 2, 7 or 9 (GND) | + VBATT (pin 1) for LIN pull-up |
| LIN3 | 10, 7 or 9 (GND) | + VBATT (pin 1) for LIN pull-up |
| LIN4 | 4, 7 or 9 (GND) | + VBATT (pin 1) for LIN pull-up |
| Power | 1 (VBATT), 7 or 9 (GND) | 12V nominal |

---

## Pin Map

### CAN Bus

| Function | GPIO | Notes |
|----------|------|-------|
| CAN1 RX | 1 | From transceiver to MCU |
| CAN1 TX | 2 | From MCU to transceiver |
| CAN1 Enable | 3 | **Active LOW** — drive low to enable transceiver |
| CAN1 Termination | 4 | **HIGH** to enable 120 ohm termination |
| CAN2 RX | 13 | From transceiver to MCU |
| CAN2 TX | 14 | From MCU to transceiver |
| CAN2 Enable | 15 | **Active LOW** — drive low to enable transceiver |
| CAN2 Termination | 12 | **HIGH** to enable 120 ohm termination |

### LIN Bus (SJA1124)

| Function | GPIO | Notes |
|----------|------|-------|
| SPI CS | 33 | Manual chip select |
| SPI SCK | 34 | Clock (CPOL=0, CPHA=1, 4 MHz max) |
| SPI MISO | 32 | Data from SJA1124 to MCU |
| SPI MOSI | 23 | Data from MCU to SJA1124 |
| STAT | 28 | SJA1124 status pin |
| INT | 26 | SJA1124 interrupt (active LOW) |
| CLK | 21 | 8 MHz clock output to SJA1124 PLL |

### Other

| Function | GPIO | Notes |
|----------|------|-------|
| Secondary Flash CS | 0 | QMI chip select for W25Q128 (CS1) |
| Boot Button | — | Directly to bootloader (active during reset) |

---

## Power Supply

| Rail | Requirement |
|------|-------------|
| Board supply | 5V or as per board design (CAN/LIN transceivers typically need 5V) |
| MCU core | 3.3V (regulated on-board) |
| CAN bus | CAN transceivers are powered from the board supply |
| LIN bus | LIN operates at battery voltage (typically 12V) — the SJA1124 transceiver handles level shifting |

Ensure a clean, stable power supply. CAN and LIN transceivers are sensitive to noise — use appropriate decoupling capacitors near the transceiver power pins.

---

## CAN Bus Wiring

### Physical Connection

CAN uses a differential two-wire bus (CAN_H and CAN_L). Connect the board's CAN transceiver output to the bus:

```
Board CAN1/CAN2          CAN Bus
┌──────────────┐         ═══════════
│  CAN_H  ─────┼────────── CAN_H
│  CAN_L  ─────┼────────── CAN_L
│  GND    ─────┼────────── GND
└──────────────┘         ═══════════
```

**Always connect GND** between the board and other CAN devices. CAN is differential but requires a common ground reference.

### Termination

CAN bus requires 120 ohm termination resistors at each end of the bus. The board has switchable termination on both CAN1 and CAN2.

**When to enable termination:**
- Enable if the board is at one end of the CAN bus
- Enable if the board is the only device on the bus (e.g., connected directly to a CAN adapter)
- Do NOT enable if the board is in the middle of an existing terminated bus

**How to enable:**
- Via the config tool: CAN tab > Termination checkbox
- Via firmware: CAN termination GPIO is set HIGH to enable

**Typical bench setup** (board + PCAN adapter):
- Board: enable termination
- PCAN-USB: enable termination (via PCAN hardware switch or jumper)

### Bitrate

Default: **500 kbps**. Configurable via the config tool (CAN tab) or config protocol.

Supported bitrates: 10K, 20K, 50K, 100K, 125K, 250K, 500K, 800K, 1M.

All devices on the same CAN bus must use the same bitrate.

### CAN1 vs CAN2

| | CAN1 | CAN2 |
|-|------|------|
| Always on | Yes | No — must be enabled via config |
| PIO | PIO0 | PIO1 |
| Config protocol | Runs on CAN1 | Not used for config |
| Bootloader | Uses CAN1 (0x700-0x7FF) | Not used |

**CAN1 is the primary bus** — the config tool and bootloader communicate over CAN1. Always connect your CAN adapter to CAN1 for configuration and firmware updates.

CAN2 is a secondary bus for gateway routing. Enable it via the config tool when needed.

---

## LIN Bus Wiring

### Physical Connection

LIN is a single-wire bus with a pull-up to battery voltage (typically 12V). The SJA1124 transceiver handles the voltage level shifting.

```
Board LIN Channel          LIN Bus
┌──────────────────┐
│  LIN1  ───────────┼────── LIN wire ──── LIN device
│  LIN2  ───────────┼────── LIN wire ──── LIN device
│  LIN3  ───────────┼────── LIN wire ──── LIN device
│  LIN4  ───────────┼────── LIN wire ──── LIN device
│  GND   ───────────┼────── GND
└──────────────────┘
```

Each LIN channel is independent — they can run at different baud rates and in different modes (master/slave).

### LIN Supply

The SJA1124 requires a battery voltage supply (typically 8-18V, nominal 12V) for the LIN transceiver stages. Check the SJA1124 datasheet (`Datasheets/SJA1124.pdf`) for exact voltage requirements.

### Master vs Slave Mode

Each LIN channel can operate as:

| Mode | Role | Schedule Table |
|------|------|----------------|
| **Master** | Controls the bus — sends headers, schedules frames | Required — define which frames to send and when |
| **Slave** | Responds to headers from the master | Not needed — responds on demand |

Configure via the config tool (LIN tab) or device profiles.

### Baud Rate

Default: **19200 bps**. Standard LIN baud rates: 9600, 19200.

All devices on the same LIN bus must use the same baud rate.

### SJA1124 Clock

The SJA1124 requires an external 8 MHz clock for its PLL. The firmware generates this on GPIO 21 using the RP2350's GPOUT peripheral (48 MHz USB PLL / 6 = 8 MHz).

This is handled automatically by the firmware — no user configuration needed.

---

## Config Tool Connection

### Requirements

| Item | Detail |
|------|--------|
| PC | Windows 10/11, .NET 8 runtime |
| CAN Adapter | PCAN-USB (recommended), Vector XL, or SLCAN device |
| Connection | Adapter ↔ CAN1 bus |
| Bitrate | Must match board's CAN1 bitrate (default 500 kbps) |

### Supported Adapters

| Adapter | Driver | Status |
|---------|--------|--------|
| PCAN-USB | Peak driver (auto-installed with PCAN-View) | Full support, recommended |
| Vector XL | Vector XL Driver Library (`vxlapi64.dll`) | Full support |
| Kvaser | Kvaser CANlib SDK (`canlib32.dll`) | Implemented but untested |
| SLCAN | System.IO.Ports (no driver needed) | Full support (CANable, USBtin) |

### Quick Start

1. Connect CAN adapter to board CAN1 (CAN_H, CAN_L, GND)
2. Enable termination on both ends
3. Power the board
4. Launch config tool: `dotnet run --project software/CanLinConfig/CanLinConfig.csproj`
5. Select adapter, channel, bitrate (500000)
6. Click **Connect**
7. Status should show "Connected" with firmware version

---

## Firmware Flashing

Firmware is flashed via CAN using the [2350Bootloader](https://github.com/ruiglopes/2350Bootloader).

### First-Time Flash (Bootloader)

The bootloader must be programmed first via SWD (debug probe) or UF2 (BOOTSEL mode):

1. Hold the boot button while powering the board
2. The RP2350 enters BOOTSEL mode (appears as a USB drive)
3. Copy the bootloader UF2 file to the drive

### Firmware Update via CAN

Once the bootloader is installed, firmware updates are done over CAN1:

1. Open the config tool
2. Click **Update Firmware**
3. Select `.bin` or `.dfw` file
4. The tool handles: bootloader entry, flash programming, verification, app restart

The board can also be forced into bootloader mode by holding the boot button during power-on.

### CAN IDs Used by Bootloader

| CAN ID | Direction | Purpose |
|--------|-----------|---------|
| 0x700 | PC → Board | Bootloader commands |
| 0x701 | Board → PC | Bootloader responses |
| 0x702 | PC → Board | Flash data |
| 0x7FF | Board → PC | Debug messages |

These IDs are active only in bootloader mode (except 0x700 which is monitored in app mode for reboot-to-bootloader commands).

---

## Bench Setup Examples

### Minimal: Board + CAN Adapter

```
┌─────────┐    CAN_H    ┌──────────┐
│  Board  │─────────────│ PCAN-USB │──── PC
│  CAN1   │─────────────│          │
│  (term) │    CAN_L    │  (term)  │
│         │─────────────│          │
│   GND   │─────────────│   GND    │
└─────────┘             └──────────┘
```
- Both ends terminated
- Config tool connects over CAN1
- CAN2 and LIN not connected

### Gateway: Board Between Two CAN Buses

```
┌──────────┐   CAN1    ┌─────────┐    CAN2   ┌──────────┐
│ Device A │───────────│  Board  │────────────│ Device B │
│  (term)  │           │         │            │  (term)  │
└──────────┘           └─────────┘            └──────────┘
```
- Board in the middle — do NOT enable board termination on either bus
- CAN1 and CAN2 both active
- Routing rules configured to pass/filter/modify frames between buses
- Config tool adapter on either CAN1 or CAN2 (CAN1 recommended)

### LIN Device: Board as LIN Master

```
┌─────────┐    LIN1     ┌────────────┐
│  Board  │─────────────│ LIN Device │
│ (master)│             │  (slave)   │
│   GND   │─────────────│    GND     │
└─────────┘             └────────────┘
```
- Board configured as LIN master on the relevant channel
- Schedule table defines which frames to send and when
- LIN pull-up resistor to battery voltage (check SJA1124 datasheet for requirements)
- Use device profiles (WDA Wiper, CWA400 Pump) for pre-configured LIN setups

---

## Troubleshooting

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| Config tool can't connect | Wrong bitrate, missing termination, CAN_H/CAN_L swapped | Check wiring, verify bitrate matches, enable termination |
| "Connected" but no diagnostics | Firmware not running (board in bootloader) | Flash firmware, or power cycle without holding boot button |
| CAN2 not working | CAN2 not enabled | Enable CAN2 in config tool (CAN tab), Write All, Save NVM |
| LIN no response | Wrong mode (master/slave), wrong baud rate, missing LIN supply voltage | Check LIN config, verify 12V supply to SJA1124, check pull-up |
| Firmware update fails | CAN adapter busy, wrong bootloader bitrate | Disconnect config tool first, try 500K/250K/125K bitrate |
| Board hangs after flash | Firmware crash, watchdog reset | Hold boot button + power cycle to enter bootloader, re-flash |
| CAN frames lost | Bus overload, missing termination | Check bus load, ensure both ends terminated, reduce frame rate |
