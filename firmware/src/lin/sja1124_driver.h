#ifndef SJA1124_DRIVER_H
#define SJA1124_DRIVER_H

#include <stdint.h>
#include <stdbool.h>
#include "hal/hal_spi.h"
#include "lin/lin_bus.h"

/* ---- SJA1124 Driver Context ---- */
typedef struct {
    hal_spi_ctx_t *spi;
    uint8_t        silicon_id;
    bool           pll_locked;
    bool           initialized;
} sja1124_ctx_t;

/* ---- Error Codes ---- */
typedef enum {
    SJA_OK = 0,
    SJA_ERR_SPI,
    SJA_ERR_PLL_LOCK,
    SJA_ERR_PLL_FREQ,
    SJA_ERR_TIMEOUT,
    SJA_ERR_INVALID_PARAM,
    SJA_ERR_NOT_INIT,
    SJA_ERR_BUSY,
} sja1124_err_t;

/**
 * Initialize the SJA1124 device.
 * Performs: clear INITI, read ID, configure PLL, wait for lock.
 * @param ctx   Driver context (caller provides storage)
 * @param spi   Initialized SPI context
 * @return SJA_OK on success
 */
sja1124_err_t sja1124_init(sja1124_ctx_t *ctx, hal_spi_ctx_t *spi);

/**
 * Reset the SJA1124 device.
 */
sja1124_err_t sja1124_reset(sja1124_ctx_t *ctx);

/**
 * Read the silicon ID register.
 * @param ctx   Driver context
 * @param id    Output: silicon ID value (bits 4:0)
 */
sja1124_err_t sja1124_read_id(sja1124_ctx_t *ctx, uint8_t *id);

/**
 * Check if PLL is locked.
 */
bool sja1124_pll_is_locked(sja1124_ctx_t *ctx);

/**
 * Configure a LIN channel.
 * Puts channel in INIT mode, sets baud rate, break length, interrupts,
 * then transitions to Normal mode.
 * @param ctx     Driver context
 * @param ch      Channel index (0-3)
 * @param config  Channel configuration
 */
sja1124_err_t sja1124_channel_init(sja1124_ctx_t *ctx, uint8_t ch,
                                    const lin_channel_config_t *config);

/**
 * Stop a LIN channel (put to sleep mode).
 */
sja1124_err_t sja1124_channel_stop(sja1124_ctx_t *ctx, uint8_t ch);

/**
 * Transmit a LIN frame (commander publish: header + response).
 * @param ctx     Driver context
 * @param ch      Channel index (0-3)
 * @param frame   Frame to transmit
 */
sja1124_err_t sja1124_frame_tx(sja1124_ctx_t *ctx, uint8_t ch,
                                const lin_frame_t *frame);

/**
 * Send a header only (commander subscribe: header, expect responder data).
 * @param ctx     Driver context
 * @param ch      Channel index (0-3)
 * @param id      6-bit LIN frame ID
 * @param dlc     Expected response length
 * @param classic_cs  Checksum type
 */
sja1124_err_t sja1124_header_tx(sja1124_ctx_t *ctx, uint8_t ch,
                                 uint8_t id, uint8_t dlc, bool classic_cs);

/**
 * Read a received frame from the get-status buffer.
 * Call after DRF interrupt indicates reception complete.
 * @param ctx     Driver context
 * @param ch      Channel index (0-3)
 * @param frame   Output: received frame data
 */
sja1124_err_t sja1124_frame_rx(sja1124_ctx_t *ctx, uint8_t ch,
                                lin_frame_t *frame);

/**
 * Read the INT3 register to determine which channels have pending interrupts.
 * @param ctx     Driver context
 * @param int3    Output: INT3 register value
 */
sja1124_err_t sja1124_read_int3(sja1124_ctx_t *ctx, uint8_t *int3);

/**
 * Read the error status (LES) for a channel.
 * @param ctx     Driver context
 * @param ch      Channel index (0-3)
 * @param les     Output: LES register value
 */
sja1124_err_t sja1124_read_error_status(sja1124_ctx_t *ctx, uint8_t ch,
                                         uint8_t *les);

/**
 * Read the status (LS) for a channel.
 */
sja1124_err_t sja1124_read_status(sja1124_ctx_t *ctx, uint8_t ch,
                                   uint8_t *ls);

/**
 * Clear error flags by writing 1s to LES register.
 */
sja1124_err_t sja1124_clear_errors(sja1124_ctx_t *ctx, uint8_t ch,
                                    uint8_t flags);

/**
 * Clear status flags by writing 1s to LS register.
 */
sja1124_err_t sja1124_clear_status(sja1124_ctx_t *ctx, uint8_t ch,
                                    uint8_t flags);

/**
 * Read the LSTATE register to get current LIN state machine state.
 */
sja1124_err_t sja1124_read_lstate(sja1124_ctx_t *ctx, uint8_t ch,
                                   uint8_t *lstate);

/**
 * Check if a channel is busy (transmission/reception in progress).
 */
bool sja1124_channel_busy(sja1124_ctx_t *ctx, uint8_t ch);

/**
 * Enable high-speed mode (>20 kBd) for a channel.
 */
sja1124_err_t sja1124_set_high_speed(sja1124_ctx_t *ctx, uint8_t ch, bool enable);

/**
 * Configure system-level interrupts (INT1EN, INT2EN, INT3EN).
 */
sja1124_err_t sja1124_configure_interrupts(sja1124_ctx_t *ctx);

#endif /* SJA1124_DRIVER_H */
