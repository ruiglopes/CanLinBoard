# CanLinConfig User Guide

Windows configuration tool for the CAN/LIN Gateway Board.

---

## Requirements

- Windows 10/11 with .NET 8 runtime
- CAN adapter: PCAN-USB, Vector XL, SLCAN device, or Kvaser
- CAN/LIN Gateway Board powered and connected to the adapter via CAN1

## Installation

Build from source:

```bash
cd software
dotnet build CanLinConfig.sln -c Release
```

The executable is at `CanLinConfig/bin/Release/net8.0-windows/CanLinConfig.exe`.

## Supported CAN Adapters

| Adapter | Driver Required | Notes |
|---------|----------------|-------|
| PCAN-USB | Peak PCAN driver (auto-installed with hardware) | Primary development adapter |
| Vector XL | Vector XL Driver Library (`vxlapi64.dll`) | Channels auto-detected from hardware |
| SLCAN | None (serial port) | CANable, USBtin, or any SLCAN-compatible device |
| Kvaser | Kvaser CANlib SDK (`canlib32.dll`) | Untested |

---

## Connecting to the Board

1. Power the CAN/LIN Gateway Board
2. Connect your CAN adapter to the board's **CAN1** bus
3. Launch CanLinConfig
4. In the connection bar at the top:
   - **Adapter**: Select your adapter type
   - **Channel**: Select the CAN channel (auto-populated from the adapter's available channels)
   - **Bitrate**: 500000 (default, must match the board's CAN1 bitrate)
5. Click **Connect**

On success, the status bar shows `Connected - FW vX.Y.Z` and the status LED turns green.

If the connection times out, verify:
- The board is powered and running firmware (not stuck in bootloader)
- CAN bus wiring and termination are correct
- The bitrate matches the board's CAN1 setting (default 500 kbps)

---

## UI Overview

### Tabs

| Tab | Purpose |
|-----|---------|
| **CAN** | CAN1 and CAN2 bus settings: bitrate, termination, enable |
| **LIN** | LIN channel 1-4 settings: enable, master/slave mode, baud rate, schedule table |
| **Routing** | Gateway routing rules editor with byte-level mapping |
| **Diagnostics Settings** | Diagnostic heartbeat configuration: CAN ID, interval, bus |
| **Live Diagnostics** | Real-time bus health dashboard and CAN frame monitor |
| **Profiles** | Device profile library (WDA wiper, CWA400 pump) with one-click apply |

### Bottom Action Bar

| Button | Action |
|--------|--------|
| **Read All** | Read all parameters from the device into the UI |
| **Write All** | Write all UI parameters to the device (RAM only, not persisted) |
| **Save NVM** | Persist current device configuration to non-volatile memory |
| **Load Defaults** | Reset all device parameters to factory defaults |
| **Enter Bootloader** | Reboot the device into bootloader mode for firmware updates |
| **Export File** | Save the current UI configuration to a JSON file |
| **Import File** | Load a configuration from a JSON file into the UI |

---

## Basic Workflow

### Reading the Current Configuration

1. Connect to the board
2. Click **Read All**
3. All tabs populate with the device's current settings

### Changing Settings

1. Modify parameters on any tab
2. Click **Write All** to send changes to the device (active in RAM)
3. Click **Save NVM** to persist changes across power cycles

Changes written without saving are lost on reboot.

### Exporting / Importing Configuration Files

Use **Export File** and **Import File** to save or load the complete configuration as a JSON file. This is useful for:
- Backing up a known-good configuration
- Sharing settings between boards
- Version-controlling configurations

---

## CAN Configuration

### CAN1
- Always enabled (cannot be disabled — it carries the config protocol)
- **Bitrate**: Default 500000. Changes take effect after reboot (CAN1 cannot be restarted at runtime because it carries the active config connection)
- **Termination**: Enable the 120-ohm termination resistor on CAN1

### CAN2
- **Enabled**: Enable/disable the CAN2 bus at runtime
- **Bitrate**: Takes effect immediately on Write All + Save
- **Termination**: Enable the 120-ohm termination resistor on CAN2

---

## LIN Configuration

Each of the 4 LIN channels can be independently configured:

- **Enabled**: Activate the channel
- **Mode**: Master or Slave
  - **Master**: The board drives the LIN bus and executes a schedule table
  - **Slave**: The board listens for headers from an external master
- **Baud Rate**: 1000-20000 (default 19200)

### Schedule Table (Master Mode Only)

When a channel is in Master mode, you must define a schedule table:

| Field | Description |
|-------|-------------|
| **ID** | LIN frame ID (0x00-0x3F) |
| **DLC** | Data length (1-8 bytes) |
| **Direction** | Publish (master sends data) or Subscribe (master requests slave data) |
| **Delay** | Time in milliseconds before sending the next entry |
| **Data** | Initial data bytes (hex) for Publish frames |

The schedule repeats continuously. For Subscribe entries, the master sends a header and waits for a slave response.

---

## Gateway Routing

Routing rules define how frames are forwarded between buses.

### Adding a Rule

1. Go to the **Routing** tab
2. Click **Add Rule**
3. Configure:
   - **Source Bus / ID / Mask**: Which frames to match. The mask ANDs with the incoming ID for range matching.
   - **Destination Bus / ID / DLC**: Where to send the frame and with what ID/length
   - **Enabled**: Toggle the rule on/off
4. Optionally add **Byte Mappings** for data transformation

### Byte Mappings

Without mappings, the entire frame is copied as-is (passthrough). With mappings, you control exactly which bytes go where:

| Field | Description |
|-------|-------------|
| **Src Byte** | Byte index (0-7) in the source frame |
| **Dst Byte** | Byte index (0-7) in the destination frame |
| **Mask** | Bit mask applied to the source byte (e.g., 0x0F for low nibble) |
| **Shift** | Bit shift after masking (positive = left, negative = right) |
| **Offset** | Value added after shift |

Formula: `dst[dst_byte] = ((src[src_byte] & mask) << shift) + offset`

### Routing Examples

**Simple CAN1-to-CAN2 bridge:**
- Src Bus: CAN1, ID: 0x100, Mask: 0x7FF
- Dst Bus: CAN2, ID: 0x100, DLC: 8
- No byte mappings (full passthrough)

**CAN-to-LIN with byte extraction:**
- Src Bus: CAN1, ID: 0x300, Mask: 0x7FF
- Dst Bus: LIN1, ID: 0x20, DLC: 4
- Mapping 1: Src Byte 0 -> Dst Byte 0, Mask 0x07 (extract 3-bit mode field)
- Mapping 2: Src Byte 1 -> Dst Byte 1, Mask 0xFF (copy speed byte)

### LIN Master Channel Routing Note

When a routing rule targets a LIN channel configured as **master**, the routed data updates the matching schedule entry's data buffer rather than transmitting directly. The scheduler then sends the updated data at its scheduled time. If no matching schedule entry exists for the destination LIN ID, the routed frame is silently dropped.

This means: to route CAN frames to a master LIN channel, you must have a schedule entry with a matching LIN ID and Direction set to Publish.

---

## Device Profiles

Profiles are pre-built configurations for specific LIN devices. They bundle LIN channel settings, schedule tables, routing rules, and device parameters into a one-click setup.

### Built-in Profiles

| Profile | Device | LIN Schedule |
|---------|--------|-------------|
| **WDA Wiper** | Bosch Motorsport WDA LIN wiper motor | 2 entries: control (publish) + status (subscribe) |
| **CWA400 Pump** | Pierburg CWA400 LIN coolant pump | 2 entries: control (publish) + status (subscribe) |

### Applying a Profile

1. Go to the **Profiles** tab
2. Select a profile from the list
3. Choose the **LIN Channel** to assign it to (LIN1-4)
4. Click **Apply Profile**

This generates:
- LIN channel config (master mode, correct baud rate)
- Schedule table entries
- CAN-to-LIN and LIN-to-CAN routing rules with byte mappings

5. Click **Write All** then **Save NVM** to persist

### Device Parameters

After applying a profile, parameter controls appear (dropdowns for enum values, sliders for numeric values). Use **Send Control Frame** to send a CAN frame with the current parameter values to the device.

### Custom Profiles

Profiles are JSON files stored alongside the application. You can:
- **Export** a profile to share it
- **Import** a profile from a JSON file
- Create your own by copying and editing an existing profile JSON

### Profile JSON Format

```json
{
  "name": "Device Name",
  "id": "unique-id",
  "version": 1,
  "lin_config": {
    "mode": "master",
    "baudrate": 19200
  },
  "schedule_table": [
    { "id": "0x20", "direction": "publish", "dlc": 4, "interval_ms": 20 },
    { "id": "0x21", "direction": "subscribe", "dlc": 4, "interval_ms": 20 }
  ],
  "parameters": [
    {
      "name": "Parameter Name",
      "type": "enum",
      "options": ["Off", "On"],
      "frame_id": "0x20",
      "byte": 0,
      "mask": "0xFF"
    }
  ],
  "can_control": {
    "rx_id": "0x300",
    "mappings": [
      { "can_byte": 0, "param": "Parameter Name" }
    ]
  },
  "can_status": {
    "tx_id": "0x301",
    "mappings": [
      { "lin_frame": "0x21", "lin_byte": 0, "can_byte": 0 }
    ]
  }
}
```

| Field | Description |
|-------|-------------|
| `lin_config` | LIN channel settings (mode and baud rate) |
| `schedule_table` | Master schedule entries with frame IDs, direction, DLC, and timing |
| `parameters` | User-controllable device parameters mapped to LIN frame bytes |
| `can_control` | CAN-to-LIN control mapping (incoming CAN frame drives LIN parameters) |
| `can_status` | LIN-to-CAN status mapping (LIN responses forwarded as CAN frames) |

---

## Live Diagnostics

The **Live Diagnostics** tab shows real-time information from the board's diagnostic heartbeat:

| Panel | Content |
|-------|---------|
| System State | BOOT / OK / WARN / ERROR |
| Uptime | Seconds since boot |
| MCU Temperature | RP2350 die temperature |
| Reset Reason | PowerOn / WatchdogTimeout / CrashReboot |
| CAN1/CAN2 Stats | RX frame counts and error counts |
| LIN1-4 Stats | RX frame counts and error counts per channel |
| Gateway Stats | Total frames routed |
| Heap / Stack | Free heap (KB) and minimum stack watermark (words) |
| Frame Monitor | Scrolling log of raw CAN frames with timestamp, ID, DLC, and data |

The diagnostics heartbeat is configurable on the **Diagnostics Settings** tab (CAN ID, interval, bus, enable/disable).

---

## Firmware Updates

To update the board firmware:

1. Click **Enter Bootloader** (the board reboots into bootloader mode)
2. Use the CAN Flash Programmer tool or `can_flash.py` to flash the new firmware binary
3. The board reboots into the new firmware automatically
4. Reconnect in CanLinConfig

Alternatively, hold the boot button (GPIO 5) while power-cycling the board to enter bootloader mode without software.

---

## Troubleshooting

**"Connection timed out"**
- Verify the board is powered and running firmware
- Check CAN wiring and termination
- Ensure the bitrate matches (default 500 kbps)

**"Read All" shows unexpected values**
- Click **Load Defaults** then **Save NVM** to reset to factory settings

**Routing rules not working**
- Verify the rule is **Enabled**
- Check that source and destination buses are active
- For LIN master destinations, ensure a matching schedule entry exists

**LIN device not responding**
- Verify the LIN channel is enabled in Master mode
- Check the baud rate matches the device (typically 19200)
- Ensure the schedule table has entries for the device's frame IDs

**Profile apply has no effect**
- After applying, click **Write All** then **Save NVM**
- Check the LIN tab to verify the channel was configured correctly

**Board unresponsive**
- Hold the boot button while power-cycling to enter bootloader mode
- Re-flash the firmware using the CAN Flash Programmer
