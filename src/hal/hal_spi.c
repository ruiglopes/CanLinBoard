#include "hal/hal_spi.h"
#include "board_config.h"
#include "hardware/spi.h"
#include "hardware/gpio.h"

/*
 * SJA1124 SPI protocol:
 *   Byte 0: Address (8-bit register address)
 *   Byte 1: Control (bit7=RO, bits3:0=DLC where DLC=num_bytes-1)
 *   Byte 2+: Data bytes
 *
 * Manual CS management required for proper framing.
 * Min 250 ns between CS deassert and next assert (t_WH(S)).
 */

static inline void cs_select(void)
{
    gpio_put(LIN_CS_PIN, 0);
}

static inline void cs_deselect(void)
{
    gpio_put(LIN_CS_PIN, 1);
}

void hal_spi_init(hal_spi_ctx_t *ctx)
{
    /* Initialize SPI peripheral at 4 MHz, mode 1 (CPOL=0, CPHA=1) */
    spi_init(LIN_SPI_PORT, LIN_SPI_BAUDRATE);
    spi_set_format(LIN_SPI_PORT, 8, SPI_CPOL_0, SPI_CPHA_1, SPI_MSB_FIRST);

    /* Configure GPIO functions for SPI */
    gpio_set_function(LIN_SCK_PIN, GPIO_FUNC_SPI);
    gpio_set_function(LIN_MOSI_PIN, GPIO_FUNC_SPI);
    gpio_set_function(LIN_MISO_PIN, GPIO_FUNC_SPI);

    /* Manual CS — configure as GPIO output, start deselected (HIGH) */
    gpio_init(LIN_CS_PIN);
    gpio_set_dir(LIN_CS_PIN, GPIO_OUT);
    gpio_put(LIN_CS_PIN, 1);

    /* Create FreeRTOS mutex for SPI access protection */
    ctx->mutex = xSemaphoreCreateMutex();
    ctx->initialized = true;
}

bool hal_spi_read_reg(hal_spi_ctx_t *ctx, uint8_t addr, uint8_t *buf, size_t len)
{
    if (!ctx->initialized || len == 0 || len > 16) return false;

    uint8_t cmd[2];
    cmd[0] = addr;                              /* Register address */
    cmd[1] = 0x80 | ((uint8_t)(len - 1));       /* RO=1, DLC=len-1 */

    /* Prepare TX buffer: cmd + dummy bytes for read */
    uint8_t tx_buf[18] = {0};
    uint8_t rx_buf[18] = {0};
    tx_buf[0] = cmd[0];
    tx_buf[1] = cmd[1];
    /* Remaining bytes are zeros (dummy for clocking out data) */

    size_t total = 2 + len;

    xSemaphoreTake(ctx->mutex, portMAX_DELAY);

    cs_select();
    spi_write_read_blocking(LIN_SPI_PORT, tx_buf, rx_buf, total);
    cs_deselect();

    xSemaphoreGive(ctx->mutex);

    /* Data starts at byte 2 of RX buffer */
    for (size_t i = 0; i < len; i++) {
        buf[i] = rx_buf[2 + i];
    }

    return true;
}

bool hal_spi_write_reg(hal_spi_ctx_t *ctx, uint8_t addr, const uint8_t *data, size_t len)
{
    if (!ctx->initialized || len == 0 || len > 16) return false;

    /* Build TX frame: addr + control + data */
    uint8_t tx_buf[18];
    tx_buf[0] = addr;                          /* Register address */
    tx_buf[1] = (uint8_t)(len - 1);            /* RO=0, DLC=len-1 */
    for (size_t i = 0; i < len; i++) {
        tx_buf[2 + i] = data[i];
    }

    size_t total = 2 + len;

    xSemaphoreTake(ctx->mutex, portMAX_DELAY);

    cs_select();
    spi_write_blocking(LIN_SPI_PORT, tx_buf, total);
    cs_deselect();

    xSemaphoreGive(ctx->mutex);

    return true;
}

void hal_spi_lock(hal_spi_ctx_t *ctx)
{
    xSemaphoreTake(ctx->mutex, portMAX_DELAY);
}

void hal_spi_unlock(hal_spi_ctx_t *ctx)
{
    xSemaphoreGive(ctx->mutex);
}
