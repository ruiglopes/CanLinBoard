# Application Development Guide

How to build firmware that runs on an RP2350 board managed by this CAN bootloader.

## Overview

The bootloader occupies the first 32 KB of flash. Your application lives at `0x10008000` onwards, preceded by a mandatory 256-byte header. The bootloader validates the header (magic, CRC32) before jumping to your code.

```
Flash Address         Contents
─────────────────────────────────────
0x10000000            Bootloader code (28 KB)
0x10007000            Bootloader config / NVM (4 KB)
0x10008000            Application header (256 bytes)
0x10008100            Application code (your firmware)
  ...
0x10400000            End of 4 MB flash
```

The reference application is in `test-app/` — use it as a starting point.

## Prerequisites

- Pico SDK 2.1.1 at `~/.pico-sdk/sdk/2.1.1`
- ARM GCC toolchain: `arm-none-eabi-gcc` (tested with 12.2)
- CMake 3.13+, Ninja build system
- Python 3 (for the header prepend script)
- picotool at `~/.pico-sdk/picotool/2.1.1/picotool`

## Quick Start

Copy `test-app/` as a template and modify it:

```bash
cp -r test-app/ my-app/
cd my-app
# Edit src/main.c with your application code
# Edit CMakeLists.txt to rename the target and add your source files
mkdir build && cd build
cmake -DPICO_SDK_PATH="$HOME/.pico-sdk/sdk/2.1.1" \
      -Dpicotool_DIR="$HOME/.pico-sdk/picotool/2.1.1/picotool" ..
ninja
```

The build produces `my_app_fw.bin` — flash it via the CAN programmer or `tools/can_flash.py`.

## Project Structure

A minimal project needs these files:

```
my-app/
├── CMakeLists.txt              # Build configuration
├── pico_sdk_import.cmake       # Copy from Pico SDK or test-app
├── linker/
│   └── app.ld                  # Custom linker script
├── tools/
│   └── prepend_header.py       # Copy from test-app/tools/
├── include/                    # (optional) Your headers
└── src/
    └── main.c                  # Application entry point
```

## CMakeLists.txt

Key requirements:

1. **Platform**: Must target `rp2350-arm-s` (ARM Cortex-M33 Secure mode)
2. **CAN sources**: Reuse the bootloader's CAN driver and can2040 library
3. **Linker script**: Must use a custom linker script with FLASH origin at `0x10008100`
4. **Header generation**: Post-build step to prepend the 256-byte app header
5. **No stdio**: Disable USB and UART stdio (unless your app specifically needs them)

```cmake
cmake_minimum_required(VERSION 3.13)

set(PICO_PLATFORM rp2350-arm-s)
set(PICO_BOARD none)

include(pico_sdk_import.cmake)

project(my_app C CXX ASM)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)

pico_sdk_init()

# Path to the bootloader directory (adjust as needed)
set(BL_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../bootloader)

add_executable(my_app
    src/main.c
    # Add your source files here
    ${BL_DIR}/src/can.c                         # CAN driver (reuse from bootloader)
    ${BL_DIR}/lib/can2040/src/can2040.c         # can2040 library
)

target_include_directories(my_app PRIVATE
    include
    ${BL_DIR}/include                           # bl_defs.h, can.h
    ${BL_DIR}/lib/can2040/src                   # can2040.h
)

target_link_libraries(my_app
    pico_stdlib
    hardware_gpio
    hardware_watchdog
    hardware_resets
    hardware_pio
    hardware_dma
    hardware_irq
    hardware_clocks
    hardware_sync
    cmsis_core
)

# Custom linker script — FLASH starts at 0x10008100
pico_set_linker_script(my_app ${CMAKE_CURRENT_SOURCE_DIR}/linker/app.ld)

# Disable stdio (CAN is the communication channel)
pico_enable_stdio_usb(my_app 0)
pico_enable_stdio_uart(my_app 0)

# Generate .elf, .bin, .uf2, .hex
pico_add_extra_outputs(my_app)

# Prepend 256-byte header → my_app_fw.bin
find_package(Python3 REQUIRED COMPONENTS Interpreter)
add_custom_command(TARGET my_app POST_BUILD
    COMMAND ${Python3_EXECUTABLE}
        ${CMAKE_CURRENT_SOURCE_DIR}/tools/prepend_header.py
        ${CMAKE_CURRENT_BINARY_DIR}/my_app.bin
        ${CMAKE_CURRENT_BINARY_DIR}/my_app_fw.bin
        1 0 0                                   # Version: major minor patch
    COMMENT "Prepending application header → my_app_fw.bin"
)
```

## Linker Script

Copy `test-app/linker/test_app.ld` to your project and rename it. The critical part is the `MEMORY` block:

```
MEMORY
{
    FLASH(rx)     : ORIGIN = 0x10008100, LENGTH = 4M - 0x8100
    RAM(rwx)      : ORIGIN = 0x20000000, LENGTH = 512k
    SCRATCH_X(rwx): ORIGIN = 0x20080000, LENGTH = 4k
    SCRATCH_Y(rwx): ORIGIN = 0x20081000, LENGTH = 4k
}
```

Key points:
- `FLASH` starts at `0x10008100`, not `0x10000000` — the first 32 KB + 256 bytes are reserved
- No `boot2` section — the bootloader handles second-stage flash initialization
- Stack is placed at the top of `SCRATCH_Y` (`0x20082000`)
- Most code runs from flash via XIP (eXecute In Place)

**Do not modify the FLASH origin.** The bootloader expects the application vector table at `0x10008100`.

## Application Header

Every firmware binary must be prepended with a 256-byte header before flashing. The build system handles this automatically via `prepend_header.py`.

```
Offset  Size  Field         Value
──────────────────────────────────────────────
0x00    4     magic         0x41505001 ("APP\x01")
0x04    4     version       (major << 16) | (minor << 8) | patch
0x08    4     size          Application binary size in bytes
0x0C    4     crc32         CRC32 of the application binary data
0x10    4     entry_point   0x10008100
0x14    32    fw_hmac       HMAC-SHA256 signature (all 0xFF if unsigned)
0x34    204   reserved      0xFF padding
```

When authentication is active on the bootloader, the `fw_hmac` field must contain a valid HMAC-SHA256 signature. Use `prepend_header.py --key <keyfile>` to sign firmware during the build.

The bootloader validates magic, size bounds, and CRC32 before jumping to the application. If validation fails, the bootloader stays in bootloader mode and waits for a CAN connection.

## CAN Driver API

Your application reuses the bootloader's CAN driver. Include `can.h` and `bl_defs.h`:

```c
#include "bl_defs.h"
#include "can.h"
```

### Functions

| Function | Description |
|----------|-------------|
| `can_init(uint32_t bitrate)` | Initialize CAN on PIO0 with GPIO1 (RX), GPIO2 (TX), GPIO3 (enable) |
| `can_send(uint32_t id, const uint8_t *data, uint8_t len)` | Non-blocking transmit. Returns 0 on success. |
| `can_set_callback(can_rx_callback_t cb)` | Register RX callback (called from main loop, not IRQ) |
| `can_process(void)` | Drain RX ring buffer and invoke callback. Call in main loop. |
| `can_tx_ready(void)` | Returns true if TX queue has space |
| `can_tx_idle(void)` | Returns true if TX queue is completely empty |

### Usage Pattern

```c
static void on_can_rx(uint32_t id, const uint8_t *data, uint8_t len)
{
    // Handle incoming CAN messages
}

int main(void)
{
    can_set_callback(on_can_rx);
    can_init(CAN_DEFAULT_BITRATE);  // 500 kbit/s

    while (true) {
        can_process();  // Must call regularly to drain RX queue
        // ... your application logic ...
    }
}
```

### Important Constraints

- **PIO0 is reserved for CAN** — use PIO1 or PIO2 for other PIO peripherals
- The CAN RX ring buffer holds 16 messages. Call `can_process()` frequently to avoid drops.
- `can_send()` is non-blocking. The can2040 TX queue holds 4 messages.
- Before any operation that disables interrupts for an extended time (e.g., flash writes), call `can_tx_idle()` and wait until it returns true. A 50ms interrupt blackout while can2040 is mid-transmission will corrupt the PIO state machine.

## Hardware Pin Assignments

These are fixed by the PCB design and shared between bootloader and application:

| GPIO | Function | Direction | Notes |
|------|----------|-----------|-------|
| 1    | CAN RX   | Input     | Connected to CAN transceiver |
| 2    | CAN TX   | Output    | Connected to CAN transceiver |
| 3    | CAN Enable | Output  | Active low — `can_init()` drives it low |
| 5    | Boot Button | Input  | Active low with internal pull-up |

## Terminology: APP_BASE vs Application Code Start

Two addresses are frequently referenced:

| Address | Name | Description |
|---------|------|-------------|
| `0x10008000` | `APP_BASE` | Start of the **application header** (256-byte `app_header_t`). This is where the bootloader reads the header for validation. |
| `0x10008100` | App code start | Start of the **application binary code** (vector table, executable). This is the linker FLASH origin and the entry point. |

`APP_BASE` in the bootloader source (`bl_defs.h`) always refers to `0x10008000`. The application's FLASH origin in the linker script is always `0x10008100`. When the CAN protocol refers to "app base" or "application start address" in commands like `CMD_ERASE` and `CMD_DATA`, it means `APP_BASE` (`0x10008000`) — the header-inclusive address.

## CAN Bus IDs

The bootloader uses four fixed CAN arbitration IDs. Your application can use these same IDs or define its own for application-specific messages.

| CAN ID | Direction | Bootloader Use | Application Use |
|--------|-----------|----------------|-----------------|
| 0x700  | Host → Device | Commands | Receive commands / reboot trigger |
| 0x701  | Device → Host | Responses + heartbeat | Send responses / heartbeat |
| 0x702  | Host → Device | Data frames | Available for app use |
| 0x7FF  | Device → Host | Debug output | Available for app use |

### 29-Bit Extended CAN IDs

The bootloader supports extended 29-bit CAN IDs via config parameter `can_id_mode` (0x02). When set to `1`, the bootloader and host use 29-bit arbitration IDs instead of standard 11-bit IDs. The same ID values (0x700, 0x701, 0x702, 0x7FF) are used — only the frame format changes.

If your application shares the CAN bus with the bootloader and uses the same CAN driver, ensure your application's CAN ID mode matches the bootloader's configuration. Read the config parameter at startup or hard-code it to match your deployment.

**Note:** Changing `can_id_mode` takes effect after the next reset. The change is persisted in NVM.

## Implementing Reboot-to-Bootloader

This is the most important integration point. It allows the host programmer to switch a running application back into bootloader mode for firmware updates — without requiring physical access to the boot button.

### How It Works

1. The host sends a `CMD_RESET` frame on CAN ID `0x700` with the bootloader unlock key
2. Your application validates the key, writes an SRAM magic word, and triggers a watchdog reset
3. The bootloader finds the magic word on startup and enters bootloader mode instead of jumping to your app

### Constants

```c
/* From bl_defs.h */
#define SRAM_MAGIC_VALUE    0xB00710ADU
#define SRAM_MAGIC_ADDR     (0x20000000 + (512 * 1024) - 4)  /* 0x2007FFFC */
#define CMD_RESET           0x05
#define RESET_MODE_BOOTLOADER 0x01
#define RESET_UNLOCK_KEY    0xB007CAFEU
```

### SRAM Magic Address

The magic word is stored at `0x2007FFFC` — the last 4 bytes of the RP2350's 512 KB main SRAM. This address:
- Is above the stack (which grows down from `SCRATCH_Y` at `0x20082000`)
- Is not used by the Pico SDK runtime
- Survives watchdog and soft resets (SRAM contents are preserved)
- Is checked and cleared by the bootloader on every startup

### Implementation

Add this to your CAN receive callback:

```c
#include "bl_defs.h"
#include "can.h"
#include "hardware/watchdog.h"

static void on_can_rx(uint32_t id, const uint8_t *data, uint8_t len)
{
    /* Check for reboot-to-bootloader command */
    if (id == CAN_ID_BL_CMD && len >= 6
            && data[0] == CMD_RESET
            && data[1] == RESET_MODE_BOOTLOADER) {
        /*
         * Validate the 4-byte unlock key at data[2..5].
         * The key is 0xB007CAFE stored little-endian: FE CA 07 B0.
         * This prevents accidental resets from stray CAN traffic.
         */
        uint32_t key = (uint32_t)data[2]
                     | ((uint32_t)data[3] << 8)
                     | ((uint32_t)data[4] << 16)
                     | ((uint32_t)data[5] << 24);
        if (key != RESET_UNLOCK_KEY)
            return;  /* Wrong key — ignore */

        /* Write SRAM magic so bootloader enters bootloader mode */
        volatile uint32_t *magic = (volatile uint32_t *)SRAM_MAGIC_ADDR;
        *magic = SRAM_MAGIC_VALUE;

        /* Trigger immediate watchdog reset */
        watchdog_reboot(0, 0, 0);
        while (true)
            tight_loop_contents();
    }

    /* ... handle other messages ... */
}
```

### CMD_RESET Frame Format

The complete frame is 6 bytes on CAN ID `0x700`:

```
Byte  Value  Description
────────────────────────────────
[0]   0x05   CMD_RESET
[1]   0x01   RESET_MODE_BOOTLOADER
[2]   0xFE   ┐
[3]   0xCA   │ Unlock key 0xB007CAFE
[4]   0x07   │ (little-endian)
[5]   0xB0   ┘
```

The unlock key prevents accidental resets. On a busy CAN bus, a random 2-byte pattern `[0x05, 0x01]` could occur by chance. The 4-byte key makes the probability of an accidental match approximately 1 in 2^32.

### Why `volatile`?

The magic address pointer must be declared `volatile` to prevent the compiler from optimizing away the write. Since `watchdog_reboot()` is `noreturn`, the compiler might otherwise reason that the store has no observable effect and remove it.

### What the Bootloader Does on Startup

The bootloader's boot decision state machine runs on every reset:

1. **Check SRAM magic** — if `0x2007FFFC` contains `0xB00710AD`, clear it and enter bootloader mode
2. **Check boot button** — if GPIO 5 is held low, enter bootloader mode
3. **Validate application** — check header magic, size, and CRC32
4. **CAN wait window** — if app is valid, wait up to 2 seconds (configurable) for a `CMD_CONNECT` on CAN before jumping to the app
5. **Jump to app** — set VTOR, load SP, branch to reset handler

If your application implements the reboot-to-bootloader handler, the programmer tool can seamlessly switch between app mode and bootloader mode without touching the hardware.

## Complete Minimal Application

Here is a complete working example:

```c
/* Minimal application for the RP2350 CAN bootloader */

#include "pico/stdlib.h"
#include "hardware/gpio.h"
#include "hardware/watchdog.h"
#include "bl_defs.h"
#include "can.h"

#define APP_VERSION_MAJOR  1
#define APP_VERSION_MINOR  0
#define APP_VERSION_PATCH  0

#define HEARTBEAT_INTERVAL_MS  1000

static absolute_time_t next_heartbeat;

/* Send a heartbeat frame every second */
static void send_heartbeat(void)
{
    uint8_t data[8] = {0};
    data[0] = 0xAA;                /* Application heartbeat marker */
    data[1] = APP_VERSION_MAJOR;
    data[2] = APP_VERSION_MINOR;
    data[3] = APP_VERSION_PATCH;
    data[4] = gpio_get(PIN_BOOT_BUTTON) ? 0 : 1;

    can_send(CAN_ID_BL_RESP, data, 8);
}

/* CAN receive handler — runs from main loop via can_process() */
static void on_can_rx(uint32_t id, const uint8_t *data, uint8_t len)
{
    if (id == CAN_ID_BL_RESP || id == CAN_ID_BL_DEBUG)
        return;  /* Ignore our own frames */

    /* Reboot-to-bootloader with unlock key validation */
    if (id == CAN_ID_BL_CMD && len >= 6
            && data[0] == CMD_RESET
            && data[1] == RESET_MODE_BOOTLOADER) {
        uint32_t key = (uint32_t)data[2]
                     | ((uint32_t)data[3] << 8)
                     | ((uint32_t)data[4] << 16)
                     | ((uint32_t)data[5] << 24);
        if (key != RESET_UNLOCK_KEY)
            return;

        volatile uint32_t *magic = (volatile uint32_t *)SRAM_MAGIC_ADDR;
        *magic = SRAM_MAGIC_VALUE;
        watchdog_reboot(0, 0, 0);
        while (true)
            tight_loop_contents();
    }

    /* Echo any other command frame back */
    can_send(CAN_ID_BL_RESP, data, len);
}

int main(void)
{
    /* Initialize boot button GPIO */
    gpio_init(PIN_BOOT_BUTTON);
    gpio_set_dir(PIN_BOOT_BUTTON, GPIO_IN);
    gpio_pull_up(PIN_BOOT_BUTTON);

    /* Initialize CAN bus */
    can_set_callback(on_can_rx);
    can_init(CAN_DEFAULT_BITRATE);

    next_heartbeat = make_timeout_time_ms(HEARTBEAT_INTERVAL_MS);

    /* Non-blocking main loop */
    while (true) {
        can_process();

        if (time_reached(next_heartbeat)) {
            send_heartbeat();
            next_heartbeat = make_timeout_time_ms(HEARTBEAT_INTERVAL_MS);
        }
    }
}
```

## Building and Flashing

### Build

```bash
cd my-app/build
cmake -DPICO_SDK_PATH="$HOME/.pico-sdk/sdk/2.1.1" \
      -Dpicotool_DIR="$HOME/.pico-sdk/picotool/2.1.1/picotool" ..
ninja
```

Output files:
- `my_app.elf` — linked executable (for debugging)
- `my_app.bin` — raw binary (no header)
- `my_app_fw.bin` — binary with 256-byte header (flash this one)
- `my_app.uf2` — UF2 for drag-and-drop (USB boot only, not CAN)

### Flash via CAN

Using the Python tool:

```bash
python tools/can_flash.py my-app/build/my_app_fw.bin
```

Using the Windows programmer:

1. Open CAN Flash Programmer
2. Connect to the bootloader
3. Browse to `my_app_fw.bin`
4. Click "Flash Firmware"

### First-Time Flash (No Application Installed)

If the board has no valid application, the bootloader stays in bootloader mode automatically — you can connect and flash immediately.

### Updating a Running Application

If the board is running your application (not in bootloader mode):

1. Click "Reboot to BL" in the programmer, or
2. Hold the boot button (GPIO 5) and power-cycle, or
3. Send the `CMD_RESET` frame manually on the CAN bus

## Design Rules

1. **Non-blocking main loop** — call `can_process()` frequently. If you block for more than a few milliseconds, CAN messages will be dropped.

2. **PIO0 is off-limits** — the CAN driver uses PIO0 exclusively. Use PIO1 or PIO2 for NeoPixels, serial protocols, or other PIO peripherals.

3. **No boot2 section** — the bootloader already configures the flash. Your linker script must not include a boot2 section.

4. **FLASH origin is `0x10008100`** — not `0x10000000`. The bootloader and header occupy the space below.

5. **Always implement reboot-to-bootloader** — without it, the only way to re-enter bootloader mode is physical access to the boot button. The implementation is a few lines of code and enables seamless over-CAN updates.

6. **Validate the unlock key** — always check the 4-byte key before writing the SRAM magic. This prevents accidental resets from stray CAN bus traffic.

7. **Do not write to `0x2007FFFC`** — this address is reserved for the bootloader SRAM magic. Do not use it for application data.

8. **Flash operations need TX drain** — if your application writes to flash, wait for `can_tx_idle()` first. Flash erase disables interrupts for ~50ms, which will corrupt can2040 if it's mid-transmission.

## Troubleshooting

**Build fails with "PICO_SDK_PATH not set"**
Pass `-DPICO_SDK_PATH="$HOME/.pico-sdk/sdk/2.1.1"` to cmake.

**Build fails with picotool errors**
Pass `-Dpicotool_DIR="$HOME/.pico-sdk/picotool/2.1.1/picotool"` to cmake.

**Bootloader won't jump to application**
The header validation is failing. Check:
- Header magic is `0x41505001`
- CRC32 matches the binary data
- Binary size in header matches actual size
- Entry point is `0x10008100`

**Application crashes immediately after boot**
- Verify the linker script FLASH origin is `0x10008100`
- Ensure no boot2 section is present
- Check that the vector table is at the start of the binary (`.vectors` section)

**CAN messages not received**
- Ensure `can_process()` is called in the main loop
- Check that CAN transceiver enable (GPIO 3) is driven low by `can_init()`
- Verify CAN bitrate matches the bus (default 500 kbit/s)

**Reboot-to-bootloader doesn't work**
- Verify the full 6-byte frame is sent: `[0x05, 0x01, 0xFE, 0xCA, 0x07, 0xB0]`
- Check the unlock key byte order (little-endian)
- Ensure the frame is sent on CAN ID `0x700`
