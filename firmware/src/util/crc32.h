#ifndef CRC32_H
#define CRC32_H

#include <stdint.h>
#include <stddef.h>

/**
 * Compute CRC32 over a block of data.
 * Uses the standard Ethernet polynomial (0x04C11DB7).
 *
 * @param data  Pointer to data
 * @param len   Number of bytes
 * @return      CRC32 value
 */
uint32_t crc32_compute(const void *data, size_t len);

#endif /* CRC32_H */
