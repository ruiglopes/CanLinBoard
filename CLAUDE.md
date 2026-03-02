# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Firmware for a custom CAN/LIN gateway board based on the **RP2350 MCU**. The board bridges CAN and LIN buses, acting as a configurable gateway that can pass through, filter, or modify frames in transit. An external Windows configuration tool communicates with the board.

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

## Reference Projects

- DingoPDM firmware (similar concept): https://github.com/corygrant/DingoPDM_FW
- DingoConfig (external config tool): https://github.com/corygrant/dingoConfig
