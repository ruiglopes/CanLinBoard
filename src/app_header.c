#include "app_header.h"
#include "board_config.h"
#include <string.h>

/*
 * Application header placed at 0x10008000 by the linker script.
 * The size and crc32 fields are patched post-build by tools/patch_header.py.
 * The entry_point is informational (bootloader uses VTOR at 0x10008100).
 */
const app_header_t app_header __attribute__((section(".app_header"), used)) = {
    .magic       = APP_HEADER_MAGIC,
    .version     = FW_VERSION_PACKED,
    .size        = 0xFFFFFFFF,      /* Patched post-build */
    .crc32       = 0xFFFFFFFF,      /* Patched post-build */
    .entry_point = APP_CODE_BASE,   /* 0x10008100 */
    .reserved    = { [0 ... 235] = 0xFF },
};
