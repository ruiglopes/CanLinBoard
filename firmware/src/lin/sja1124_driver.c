#include "lin/sja1124_driver.h"
#include "lin/sja1124_regs.h"
#include "board_config.h"
#include "FreeRTOS.h"
#include "task.h"
#include <string.h>

/* ---- Internal Helpers ---- */

static inline uint8_t ch_reg(uint8_t ch, uint8_t offset)
{
    return SJA_CH_BASE(ch) + offset;
}

static sja1124_err_t reg_read(sja1124_ctx_t *ctx, uint8_t addr, uint8_t *val)
{
    if (!hal_spi_read_reg(ctx->spi, addr, val, 1)) return SJA_ERR_SPI;
    return SJA_OK;
}

static sja1124_err_t reg_write(sja1124_ctx_t *ctx, uint8_t addr, uint8_t val)
{
    if (!hal_spi_write_reg(ctx->spi, addr, &val, 1)) return SJA_ERR_SPI;
    return SJA_OK;
}

static sja1124_err_t reg_read_multi(sja1124_ctx_t *ctx, uint8_t addr,
                                     uint8_t *buf, size_t len)
{
    if (!hal_spi_read_reg(ctx->spi, addr, buf, len)) return SJA_ERR_SPI;
    return SJA_OK;
}

static sja1124_err_t reg_write_multi(sja1124_ctx_t *ctx, uint8_t addr,
                                      const uint8_t *buf, size_t len)
{
    if (!hal_spi_write_reg(ctx->spi, addr, buf, len)) return SJA_ERR_SPI;
    return SJA_OK;
}

/* Calculate baud rate registers from desired baud rate and PLL output freq */
static void calc_baud_regs(uint32_t baudrate, uint32_t pll_freq,
                            uint8_t *ibr_msb, uint8_t *ibr_lsb, uint8_t *fbr)
{
    /*
     * baud = pll_freq / (16 * (IBR + FBR/16))
     * => IBR + FBR/16 = pll_freq / (16 * baud)
     * => IBR_total_x16 = pll_freq / baud
     */
    uint32_t total_x16 = pll_freq / baudrate;
    uint16_t ibr = (uint16_t)(total_x16 / 16);
    uint8_t  frac = (uint8_t)(total_x16 % 16);

    *ibr_msb = (uint8_t)(ibr >> 8);
    *ibr_lsb = (uint8_t)(ibr & 0xFF);
    *fbr = frac & 0x0F;
}

/* ---- Public API ---- */

sja1124_err_t sja1124_init(sja1124_ctx_t *ctx, hal_spi_ctx_t *spi)
{
    sja1124_err_t err;

    memset(ctx, 0, sizeof(*ctx));
    ctx->spi = spi;

    /* Step 1: Clear INITI interrupt (must be done within ~3 seconds of power-up) */
    uint8_t int1;
    err = reg_read(ctx, SJA_REG_INT1, &int1);
    if (err != SJA_OK) return err;

    if (int1 & SJA_INT1_INITI) {
        err = reg_write(ctx, SJA_REG_INT1, SJA_INT1_INITI);
        if (err != SJA_OK) return err;
    }

    /* Step 2: Read and store silicon ID */
    err = sja1124_read_id(ctx, &ctx->silicon_id);
    if (err != SJA_OK) return err;

    /* Step 3: Configure PLL for 8 MHz input clock (PLLMULT=0x0A) */
    err = reg_write(ctx, SJA_REG_PLLCFG, SJA_PLLMULT_8_0_10MHZ);
    if (err != SJA_OK) return err;

    /* Step 4: Wait for PLL lock (poll STATUS.PLLIL, timeout 100 ms) */
    uint32_t start = xTaskGetTickCount();
    while ((xTaskGetTickCount() - start) < pdMS_TO_TICKS(100)) {
        uint8_t status;
        err = reg_read(ctx, SJA_REG_STATUS, &status);
        if (err != SJA_OK) return err;

        if (status & SJA_STATUS_PLLIL) {
            /* Verify no frequency fail */
            if (status & SJA_STATUS_PLLIFF) {
                return SJA_ERR_PLL_FREQ;
            }
            ctx->pll_locked = true;
            break;
        }
        vTaskDelay(pdMS_TO_TICKS(1));
    }

    if (!ctx->pll_locked) return SJA_ERR_PLL_LOCK;

    /* Step 5: Configure interrupts */
    err = sja1124_configure_interrupts(ctx);
    if (err != SJA_OK) return err;

    ctx->initialized = true;
    return SJA_OK;
}

sja1124_err_t sja1124_reset(sja1124_ctx_t *ctx)
{
    sja1124_err_t err = reg_write(ctx, SJA_REG_MODE, SJA_MODE_RST);
    if (err != SJA_OK) return err;
    ctx->initialized = false;
    ctx->pll_locked = false;
    return SJA_OK;
}

sja1124_err_t sja1124_read_id(sja1124_ctx_t *ctx, uint8_t *id)
{
    uint8_t val;
    sja1124_err_t err = reg_read(ctx, SJA_REG_ID, &val);
    if (err != SJA_OK) return err;
    *id = val & 0x1F;
    return SJA_OK;
}

bool sja1124_pll_is_locked(sja1124_ctx_t *ctx)
{
    uint8_t status;
    if (reg_read(ctx, SJA_REG_STATUS, &status) != SJA_OK) return false;
    return (status & SJA_STATUS_PLLIL) != 0;
}

sja1124_err_t sja1124_channel_init(sja1124_ctx_t *ctx, uint8_t ch,
                                    const lin_channel_config_t *config)
{
    if (ch >= LIN_CHANNEL_COUNT) return SJA_ERR_INVALID_PARAM;
    if (!ctx->initialized) return SJA_ERR_NOT_INIT;

    sja1124_err_t err;

    /* Step 1: Enter INIT mode (SLEEP=0, INIT=1) */
    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LCFG1), SJA_LCFG1_INIT);
    if (err != SJA_OK) return err;

    /* Brief delay for mode transition */
    vTaskDelay(pdMS_TO_TICKS(1));

    /* Step 2: Configure baud rate */
    uint8_t ibr_msb, ibr_lsb, fbr;
    calc_baud_regs(config->baudrate, SJA_PLL_OUTPUT_FREQ_8MHZ,
                   &ibr_msb, &ibr_lsb, &fbr);

    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LBRM), ibr_msb);
    if (err != SJA_OK) return err;
    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LBRL), ibr_lsb);
    if (err != SJA_OK) return err;
    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LFR), fbr);
    if (err != SJA_OK) return err;

    /* Step 3: Configure break length (13-bit for LIN 2.x) */
    uint8_t lcfg1 = SJA_LCFG1_INIT | (SJA_MBL_13BIT << SJA_LCFG1_MBL_SHIFT);
    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LCFG1), lcfg1);
    if (err != SJA_OK) return err;

    /* Step 4: Configure LCFG2 (IOBE=1 default, TBDE=0 for 1-bit delimiter) */
    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LCFG2), SJA_LCFG2_IOBE);
    if (err != SJA_OK) return err;

    /* Step 5: Configure idle timeout (IOT=1) */
    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LITC), SJA_LITC_IOT);
    if (err != SJA_OK) return err;

    /* Step 6: Configure response timeout (default 0x0E = 1.4x) */
    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LRTC), 0x0E);
    if (err != SJA_OK) return err;

    /* Step 7: Configure stop bits (1 stop bit) */
    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LGC), 0x00);
    if (err != SJA_OK) return err;

    /* Step 8: Enable channel interrupts (all errors + RX complete + TX complete) */
    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LIE), SJA_LIE_ALL);
    if (err != SJA_OK) return err;

    /* Step 9: High-speed mode if baud > 20 kBd */
    if (config->baudrate > 20000) {
        err = sja1124_set_high_speed(ctx, ch, true);
        if (err != SJA_OK) return err;
    }

    /* Step 10: Exit INIT mode → Normal mode (SLEEP=0, INIT=0) */
    lcfg1 = (SJA_MBL_13BIT << SJA_LCFG1_MBL_SHIFT); /* Keep MBL, clear INIT */
    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LCFG1), lcfg1);
    if (err != SJA_OK) return err;

    return SJA_OK;
}

sja1124_err_t sja1124_channel_stop(sja1124_ctx_t *ctx, uint8_t ch)
{
    if (ch >= LIN_CHANNEL_COUNT) return SJA_ERR_INVALID_PARAM;

    /* Read-modify-write to preserve MBL bits while setting INIT.
     * Use INIT mode (not SLEEP) because SLEEP can be exited by
     * a wake-up pulse on the LIN bus (floating pins = immediate wake). */
    uint8_t lcfg1;
    sja1124_err_t err = reg_read(ctx, ch_reg(ch, SJA_OFF_LCFG1), &lcfg1);
    if (err != SJA_OK) return err;

    lcfg1 = (lcfg1 & ~SJA_LCFG1_SLEEP) | SJA_LCFG1_INIT;
    return reg_write(ctx, ch_reg(ch, SJA_OFF_LCFG1), lcfg1);
}

sja1124_err_t sja1124_frame_tx(sja1124_ctx_t *ctx, uint8_t ch,
                                const lin_frame_t *frame)
{
    if (ch >= LIN_CHANNEL_COUNT) return SJA_ERR_INVALID_PARAM;
    if (frame->dlc == 0 || frame->dlc > 8) return SJA_ERR_INVALID_PARAM;

    sja1124_err_t err;

    /* Write frame ID */
    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LBI), frame->id & SJA_LBI_ID_MASK);
    if (err != SJA_OK) return err;

    /* Write buffer control: DFL, DIR=1 (TX), CCS */
    uint8_t lbc = ((frame->dlc - 1) << SJA_LBC_DFL_SHIFT) | SJA_LBC_DIR;
    if (frame->classic_cs) lbc |= SJA_LBC_CCS;
    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LBC), lbc);
    if (err != SJA_OK) return err;

    /* Write data bytes in a burst (LBD1-LBD8) */
    err = reg_write_multi(ctx, ch_reg(ch, SJA_OFF_LBD1), frame->data, frame->dlc);
    if (err != SJA_OK) return err;

    /* Trigger header transmission via LC.HTRQ */
    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LC), SJA_LC_HTRQ);
    if (err != SJA_OK) return err;

    return SJA_OK;
}

sja1124_err_t sja1124_header_tx(sja1124_ctx_t *ctx, uint8_t ch,
                                 uint8_t id, uint8_t dlc, bool classic_cs)
{
    if (ch >= LIN_CHANNEL_COUNT) return SJA_ERR_INVALID_PARAM;
    if (dlc == 0 || dlc > 8) return SJA_ERR_INVALID_PARAM;

    sja1124_err_t err;

    /* Write frame ID */
    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LBI), id & SJA_LBI_ID_MASK);
    if (err != SJA_OK) return err;

    /* Write buffer control: DFL, DIR=0 (RX/subscribe), CCS */
    uint8_t lbc = ((dlc - 1) << SJA_LBC_DFL_SHIFT);
    if (classic_cs) lbc |= SJA_LBC_CCS;
    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LBC), lbc);
    if (err != SJA_OK) return err;

    /* Trigger header transmission */
    err = reg_write(ctx, ch_reg(ch, SJA_OFF_LC), SJA_LC_HTRQ);
    if (err != SJA_OK) return err;

    return SJA_OK;
}

sja1124_err_t sja1124_frame_rx(sja1124_ctx_t *ctx, uint8_t ch,
                                lin_frame_t *frame)
{
    if (ch >= LIN_CHANNEL_COUNT) return SJA_ERR_INVALID_PARAM;

    sja1124_err_t err;

    /* Read the received ID from LBI register */
    uint8_t lbi;
    err = reg_read(ctx, ch_reg(ch, SJA_OFF_LBI), &lbi);
    if (err != SJA_OK) return err;
    frame->id = lbi & SJA_LBI_ID_MASK;

    /* Read LBC to determine DLC */
    uint8_t lbc;
    err = reg_read(ctx, ch_reg(ch, SJA_OFF_LBC), &lbc);
    if (err != SJA_OK) return err;
    frame->dlc = ((lbc & SJA_LBC_DFL_MASK) >> SJA_LBC_DFL_SHIFT) + 1;
    frame->classic_cs = (lbc & SJA_LBC_CCS) != 0;

    /* Read received data from get-status buffer */
    err = reg_read_multi(ctx, ch_reg(ch, SJA_OFF_LBD1_RX), frame->data, frame->dlc);
    if (err != SJA_OK) return err;

    /* Clear DRF and DRBNE flags */
    err = sja1124_clear_status(ctx, ch, SJA_LS_DRF | SJA_LS_DRBNE);

    return err;
}

sja1124_err_t sja1124_read_int3(sja1124_ctx_t *ctx, uint8_t *int3)
{
    return reg_read(ctx, SJA_REG_INT3, int3);
}

sja1124_err_t sja1124_read_error_status(sja1124_ctx_t *ctx, uint8_t ch,
                                         uint8_t *les)
{
    if (ch >= LIN_CHANNEL_COUNT) return SJA_ERR_INVALID_PARAM;
    return reg_read(ctx, ch_reg(ch, SJA_OFF_LES), les);
}

sja1124_err_t sja1124_read_status(sja1124_ctx_t *ctx, uint8_t ch, uint8_t *ls)
{
    if (ch >= LIN_CHANNEL_COUNT) return SJA_ERR_INVALID_PARAM;
    return reg_read(ctx, ch_reg(ch, SJA_OFF_LS), ls);
}

sja1124_err_t sja1124_clear_errors(sja1124_ctx_t *ctx, uint8_t ch, uint8_t flags)
{
    if (ch >= LIN_CHANNEL_COUNT) return SJA_ERR_INVALID_PARAM;
    return reg_write(ctx, ch_reg(ch, SJA_OFF_LES), flags);
}

sja1124_err_t sja1124_clear_status(sja1124_ctx_t *ctx, uint8_t ch, uint8_t flags)
{
    if (ch >= LIN_CHANNEL_COUNT) return SJA_ERR_INVALID_PARAM;
    return reg_write(ctx, ch_reg(ch, SJA_OFF_LS), flags);
}

sja1124_err_t sja1124_read_lstate(sja1124_ctx_t *ctx, uint8_t ch, uint8_t *lstate)
{
    if (ch >= LIN_CHANNEL_COUNT) return SJA_ERR_INVALID_PARAM;
    return reg_read(ctx, ch_reg(ch, SJA_OFF_LSTATE), lstate);
}

bool sja1124_channel_busy(sja1124_ctx_t *ctx, uint8_t ch)
{
    uint8_t lstate;
    if (sja1124_read_lstate(ctx, ch, &lstate) != SJA_OK) return true;
    uint8_t state = lstate & SJA_LSTATE_LINS_MASK;
    /* Busy if in any state other than idle */
    return (state != SJA_LINS_IDLE && state != SJA_LINS_SLEEP && state != SJA_LINS_INIT);
}

sja1124_err_t sja1124_set_high_speed(sja1124_ctx_t *ctx, uint8_t ch, bool enable)
{
    if (ch >= LIN_CHANNEL_COUNT) return SJA_ERR_INVALID_PARAM;

    uint8_t lcom1;
    sja1124_err_t err = reg_read(ctx, SJA_REG_LCOM1, &lcom1);
    if (err != SJA_OK) return err;

    if (enable) {
        lcom1 |= SJA_LCOM1_HS(ch);
    } else {
        lcom1 &= ~SJA_LCOM1_HS(ch);
    }

    return reg_write(ctx, SJA_REG_LCOM1, lcom1);
}

sja1124_err_t sja1124_configure_interrupts(sja1124_ctx_t *ctx)
{
    sja1124_err_t err;

    /* Enable PLL in-lock and out-of-lock interrupts */
    err = reg_write(ctx, SJA_REG_INT2EN,
                    SJA_INT2EN_PLLILIE | SJA_INT2EN_PLLOLIE | SJA_INT2EN_OTWIE);
    if (err != SJA_OK) return err;

    /* Enable all channel status and error interrupts */
    err = reg_write(ctx, SJA_REG_INT3EN,
                    SJA_INT3EN_L1SIE | SJA_INT3EN_L2SIE |
                    SJA_INT3EN_L3SIE | SJA_INT3EN_L4SIE |
                    SJA_INT3EN_L1EIE | SJA_INT3EN_L2EIE |
                    SJA_INT3EN_L3EIE | SJA_INT3EN_L4EIE);
    if (err != SJA_OK) return err;

    return SJA_OK;
}
