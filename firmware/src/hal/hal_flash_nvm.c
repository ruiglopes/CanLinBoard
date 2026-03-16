#include "hal/hal_flash_nvm.h"
#include "hal/hal_flash_secondary.h"
#include "board_config.h"
#include "pico.h"
#include <string.h>

/*
 * NVM storage on secondary flash (CS1).
 *
 * Uses the first 12 KB of the W25Q128 on CS1 (GPIO0):
 *   Offset 0x000000: Slot A  (4 KB)
 *   Offset 0x001000: Slot B  (4 KB)
 *   Offset 0x002000: Meta    (4 KB)
 *
 * All flash-touching functions acquire/release the QSPI bus.
 * While the bus is acquired, XIP is disabled and interrupts are masked.
 */

void hal_nvm_init(void)
{
    sec_flash_init();
}

bool __no_inline_not_in_flash_func(hal_nvm_read)(uint32_t offset, void *buf, size_t len)
{
    uint32_t irq = sec_flash_acquire_bus();
    sec_flash_read(offset, (uint8_t *)buf, len);
    sec_flash_release_bus(irq);
    return true;
}

bool __no_inline_not_in_flash_func(hal_nvm_erase_sector)(uint32_t offset)
{
    if (offset % NVM_SECTOR_SIZE != 0) return false;

    uint32_t irq = sec_flash_acquire_bus();
    bool ok = sec_flash_sector_erase(offset);
    sec_flash_release_bus(irq);

    return ok;
}

bool __no_inline_not_in_flash_func(hal_nvm_write_page)(uint32_t offset, const void *data, size_t len)
{
    if (len == 0 || len > NVM_PAGE_SIZE) return false;
    if (offset % NVM_PAGE_SIZE != 0) return false;

    uint8_t page_buf[NVM_PAGE_SIZE];
    memset(page_buf, 0xFF, NVM_PAGE_SIZE);
    memcpy(page_buf, data, len);

    uint32_t irq = sec_flash_acquire_bus();
    bool ok = sec_flash_page_program(offset, page_buf, NVM_PAGE_SIZE);
    sec_flash_release_bus(irq);

    return ok;
}

bool __no_inline_not_in_flash_func(hal_nvm_write)(uint32_t offset, const void *data, size_t len)
{
    const uint8_t *src = (const uint8_t *)data;

    uint32_t irq = sec_flash_acquire_bus();

    while (len > 0) {
        uint32_t page_offset = offset & ~(NVM_PAGE_SIZE - 1);
        uint32_t offset_in_page = offset - page_offset;
        size_t chunk = NVM_PAGE_SIZE - offset_in_page;
        if (chunk > len) chunk = len;

        uint8_t page_buf[NVM_PAGE_SIZE];
        sec_flash_read(page_offset, page_buf, NVM_PAGE_SIZE);
        memcpy(page_buf + offset_in_page, src, chunk);
        if (!sec_flash_page_program(page_offset, page_buf, NVM_PAGE_SIZE)) {
            sec_flash_release_bus(irq);
            return false;
        }

        offset += chunk;
        src += chunk;
        len -= chunk;
    }

    sec_flash_release_bus(irq);

    return true;
}
