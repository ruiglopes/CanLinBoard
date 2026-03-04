#include "hal/hal_flash_nvm.h"
#include "board_config.h"
#include "hardware/flash.h"
#include "hardware/sync.h"
#include "pico/platform.h"
#include <string.h>

/*
 * NVM storage in the tail of primary flash (CS0).
 *
 * Uses the last 12 KB of the 2 MB primary flash:
 *   Offset 0x1FD000: Slot A  (4 KB)
 *   Offset 0x1FE000: Slot B  (4 KB)
 *   Offset 0x1FF000: Meta    (4 KB)
 *
 * The Pico SDK flash_range_erase/program functions operate relative
 * to XIP_BASE (0x10000000). NVM_FLASH_OFFSET is defined in board_config.h.
 *
 * Reads use the XIP memory-mapped address directly.
 * Writes/erases use flash_range_erase/program.
 *
 * IMPORTANT: All functions that call flash_range_erase/program MUST be
 * placed in RAM via __no_inline_not_in_flash_func. The SDK flash functions
 * disable XIP during flash operations; if the caller is in flash (XIP),
 * returning to it after XIP re-enable can race with the XIP controller
 * readiness on RP2350.
 */

void hal_nvm_init(void)
{
    /* Nothing to initialize — primary flash is already configured by boot2 */
}

bool hal_nvm_read(uint32_t offset, void *buf, size_t len)
{
    const uint8_t *src = (const uint8_t *)(XIP_BASE + NVM_FLASH_OFFSET + offset);
    memcpy(buf, src, len);
    return true;
}

bool __no_inline_not_in_flash_func(hal_nvm_erase_sector)(uint32_t offset)
{
    if (offset % NVM_SECTOR_SIZE != 0) return false;

    uint32_t flash_offset = NVM_FLASH_OFFSET + offset;

    uint32_t ints = save_and_disable_interrupts();
    flash_range_erase(flash_offset, NVM_SECTOR_SIZE);
    restore_interrupts(ints);

    return true;
}

bool __no_inline_not_in_flash_func(hal_nvm_write_page)(uint32_t offset, const void *data, size_t len)
{
    if (len == 0 || len > NVM_PAGE_SIZE) return false;
    if (offset % NVM_PAGE_SIZE != 0) return false;

    uint32_t flash_offset = NVM_FLASH_OFFSET + offset;

    uint8_t page_buf[NVM_PAGE_SIZE];
    memset(page_buf, 0xFF, NVM_PAGE_SIZE);
    memcpy(page_buf, data, len);

    uint32_t ints = save_and_disable_interrupts();
    flash_range_program(flash_offset, page_buf, NVM_PAGE_SIZE);
    restore_interrupts(ints);

    return true;
}

bool __no_inline_not_in_flash_func(hal_nvm_write)(uint32_t offset, const void *data, size_t len)
{
    const uint8_t *src = (const uint8_t *)data;

    while (len > 0) {
        uint32_t page_offset = offset & ~(NVM_PAGE_SIZE - 1);
        uint32_t offset_in_page = offset - page_offset;
        size_t chunk = NVM_PAGE_SIZE - offset_in_page;
        if (chunk > len) chunk = len;

        uint8_t page_buf[NVM_PAGE_SIZE];
        if (!hal_nvm_read(page_offset, page_buf, NVM_PAGE_SIZE)) return false;
        memcpy(page_buf + offset_in_page, src, chunk);

        uint32_t flash_offset = NVM_FLASH_OFFSET + page_offset;
        uint32_t ints = save_and_disable_interrupts();
        flash_range_program(flash_offset, page_buf, NVM_PAGE_SIZE);
        restore_interrupts(ints);

        offset += chunk;
        src += chunk;
        len -= chunk;
    }

    return true;
}
