#ifndef HAL_FLASH_NVM_H
#define HAL_FLASH_NVM_H

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>

/**
 * Initialize the secondary flash (NVM) interface.
 * Called once at startup.
 */
void hal_nvm_init(void);

/**
 * Read data from NVM.
 * @param offset  Byte offset within NVM (relative to NVM base)
 * @param buf     Destination buffer
 * @param len     Number of bytes to read
 * @return true on success
 */
bool hal_nvm_read(uint32_t offset, void *buf, size_t len);

/**
 * Erase a 4 KB sector in NVM.
 * @param offset  Sector-aligned offset within NVM
 * @return true on success
 */
bool hal_nvm_erase_sector(uint32_t offset);

/**
 * Write a page (up to 256 bytes) to NVM.
 * Destination must be erased first.
 * @param offset  Page-aligned offset within NVM
 * @param data    Source data
 * @param len     Number of bytes to write (max 256)
 * @return true on success
 */
bool hal_nvm_write_page(uint32_t offset, const void *data, size_t len);

/**
 * Write arbitrary data to NVM (handles page splitting internally).
 * Destination must be erased first.
 * @param offset  Byte offset within NVM
 * @param data    Source data
 * @param len     Number of bytes to write
 * @return true on success
 */
bool hal_nvm_write(uint32_t offset, const void *data, size_t len);

#endif /* HAL_FLASH_NVM_H */
