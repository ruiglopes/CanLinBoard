#include "hal/hal_flash_nvm.h"
#include "board_config.h"
#include "hardware/flash.h"
#include "hardware/sync.h"
#include <string.h>

/*
 * NVM storage on secondary flash (XIP CS1).
 *
 * The RP2350 secondary flash is accessible via XIP at a separate address.
 * For erase/program operations we use the hardware_flash API with the
 * appropriate offset from the flash base.
 *
 * Note: The Pico SDK flash_range_erase/program functions require interrupts
 * to be disabled and operate relative to XIP_BASE (0x10000000).
 * For secondary flash we compute the offset accordingly.
 *
 * TODO: Verify CS1 flash addressing on actual hardware. If XIP CS1
 * is not directly supported, fall back to tail of primary flash.
 */

/*
 * Compute the flash offset for NVM operations.
 * Primary flash: 0x10000000 - 0x101FFFFF (2 MB)
 * Secondary flash: starts at offset 0x200000 from FLASH_BASE (tentative).
 * This may need adjustment based on actual hardware mapping.
 */
#define NVM_FLASH_OFFSET    0x200000U   /* Offset into flash address space */

void hal_nvm_init(void)
{
    /* Nothing to initialize — flash is memory-mapped via XIP */
}

bool hal_nvm_read(uint32_t offset, void *buf, size_t len)
{
    /*
     * Read directly from memory-mapped flash.
     * XIP_BASE + NVM_FLASH_OFFSET + offset gives the memory address.
     */
    const uint8_t *src = (const uint8_t *)(XIP_BASE + NVM_FLASH_OFFSET + offset);
    memcpy(buf, src, len);
    return true;
}

bool hal_nvm_erase_sector(uint32_t offset)
{
    /* Offset must be sector-aligned */
    if (offset % NVM_SECTOR_SIZE != 0) return false;

    uint32_t flash_offset = NVM_FLASH_OFFSET + offset;

    uint32_t ints = save_and_disable_interrupts();
    flash_range_erase(flash_offset, NVM_SECTOR_SIZE);
    restore_interrupts(ints);

    return true;
}

bool hal_nvm_write_page(uint32_t offset, const void *data, size_t len)
{
    if (len == 0 || len > NVM_PAGE_SIZE) return false;
    /* Offset must be page-aligned */
    if (offset % NVM_PAGE_SIZE != 0) return false;

    uint32_t flash_offset = NVM_FLASH_OFFSET + offset;

    /* flash_range_program requires page-aligned, page-sized writes.
     * Pad to full page if needed. */
    uint8_t page_buf[NVM_PAGE_SIZE];
    memset(page_buf, 0xFF, NVM_PAGE_SIZE);
    memcpy(page_buf, data, len);

    uint32_t ints = save_and_disable_interrupts();
    flash_range_program(flash_offset, page_buf, NVM_PAGE_SIZE);
    restore_interrupts(ints);

    return true;
}

bool hal_nvm_write(uint32_t offset, const void *data, size_t len)
{
    const uint8_t *src = (const uint8_t *)data;

    while (len > 0) {
        /* Align to next page boundary */
        uint32_t page_offset = offset & ~(NVM_PAGE_SIZE - 1);
        uint32_t offset_in_page = offset - page_offset;
        size_t chunk = NVM_PAGE_SIZE - offset_in_page;
        if (chunk > len) chunk = len;

        /* Read existing page, merge, write back */
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
