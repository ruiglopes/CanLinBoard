#ifndef HAL_FLASH_SECONDARY_H
#define HAL_FLASH_SECONDARY_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

/*
 * Secondary flash driver: W25Q128 on CS1 (GPIO0).
 * Accessed via QMI direct mode. Ported from FlashTest project.
 */

/* Initialize CS1 GPIO and register flash devinfo. Call once at startup. */
void sec_flash_init(void);

/* Acquire QSPI bus for CS1 direct-mode access.
 * Disables interrupts, flushes XIP cache, exits XIP mode.
 * Returns saved interrupt state (pass to release). */
uint32_t sec_flash_acquire_bus(void);

/* Release QSPI bus after CS1 access.
 * Re-enters XIP and restores interrupts. */
void sec_flash_release_bus(uint32_t irq_state);

/* --- Flash commands (call only while bus is acquired) --- */

/* Read JEDEC ID (manufacturer, device type, capacity) */
void sec_flash_read_jedec_id(uint8_t *mfr, uint8_t *type, uint8_t *cap);

/* Read data from flash at the given 24-bit address */
void sec_flash_read(uint32_t addr, uint8_t *buf, size_t len);

/* Program a page (up to 256 bytes, must not cross page boundary).
 * Returns false if flash busy timeout exceeded. */
bool sec_flash_page_program(uint32_t addr, const uint8_t *data, size_t len);

/* Erase a 4KB sector (addr must be sector-aligned).
 * Returns false if flash busy timeout exceeded. */
bool sec_flash_sector_erase(uint32_t addr);

/* Read status register */
uint8_t sec_flash_read_status(void);

#endif /* HAL_FLASH_SECONDARY_H */
