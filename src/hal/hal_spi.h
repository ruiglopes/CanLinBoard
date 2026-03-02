#ifndef HAL_SPI_H
#define HAL_SPI_H

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#include "FreeRTOS.h"
#include "semphr.h"

/**
 * SPI context for SJA1124 communication.
 * Mutex-protected for safe access from multiple FreeRTOS tasks.
 */
typedef struct {
    SemaphoreHandle_t mutex;
    bool initialized;
} hal_spi_ctx_t;

/**
 * Initialize SPI0 for SJA1124 communication.
 * Configures pins, clock (4 MHz), mode (CPOL=0, CPHA=1), manual CS.
 */
void hal_spi_init(hal_spi_ctx_t *ctx);

/**
 * Read register(s) from SJA1124.
 * @param ctx   SPI context
 * @param addr  Starting register address
 * @param buf   Buffer to receive data
 * @param len   Number of bytes to read (1-16)
 * @return true on success
 */
bool hal_spi_read_reg(hal_spi_ctx_t *ctx, uint8_t addr, uint8_t *buf, size_t len);

/**
 * Write register(s) to SJA1124.
 * @param ctx   SPI context
 * @param addr  Starting register address
 * @param data  Data to write
 * @param len   Number of bytes to write (1-16)
 * @return true on success
 */
bool hal_spi_write_reg(hal_spi_ctx_t *ctx, uint8_t addr, const uint8_t *data, size_t len);

/**
 * Acquire the SPI mutex. Blocks until available.
 */
void hal_spi_lock(hal_spi_ctx_t *ctx);

/**
 * Release the SPI mutex.
 */
void hal_spi_unlock(hal_spi_ctx_t *ctx);

#endif /* HAL_SPI_H */
