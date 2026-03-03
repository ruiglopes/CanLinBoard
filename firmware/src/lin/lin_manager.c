#include "lin/lin_manager.h"
#include "lin/sja1124_regs.h"
#include "hal/hal_spi.h"
#include "hal/hal_clock.h"
#include "hal/hal_gpio.h"
#include "board_config.h"

#include "FreeRTOS.h"
#include "task.h"
#include "queue.h"
#include "hardware/gpio.h"

#include <string.h>

/* ---- Module State ---- */
static hal_spi_ctx_t    s_spi_ctx;
static sja1124_ctx_t    s_sja_ctx;
static TaskHandle_t     s_lin_task_handle;

static QueueHandle_t    s_gateway_queue;
static QueueHandle_t    s_lin_tx_queue;

static lin_channel_config_t s_channel_config[LIN_CHANNEL_COUNT];
static lin_channel_stats_t  s_channel_stats[LIN_CHANNEL_COUNT];

static uint32_t s_temp_warning_count;
static bool     s_pll_lost_lock;

/* Master scheduling state per channel */
static struct {
    bool     active;
    uint8_t  current_index;
    uint32_t next_tick;         /* Tick count when next entry fires */
} s_schedule_state[LIN_CHANNEL_COUNT];

/* ---- GPIO Interrupt Callback (INTN pin) ---- */

static void lin_int_gpio_callback(uint gpio, uint32_t events)
{
    (void)gpio;
    (void)events;

    if (s_lin_task_handle) {
        BaseType_t xHigherPriorityTaskWoken = pdFALSE;
        vTaskNotifyGiveFromISR(s_lin_task_handle, &xHigherPriorityTaskWoken);
        portYIELD_FROM_ISR(xHigherPriorityTaskWoken);
    }
}

/* ---- Schedule Engine ---- */

static void schedule_tick(void)
{
    uint32_t now = xTaskGetTickCount();

    for (uint8_t ch = 0; ch < LIN_CHANNEL_COUNT; ch++) {
        if (!s_schedule_state[ch].active) continue;
        if (s_channel_config[ch].mode != LIN_MODE_MASTER) continue;
        if (s_channel_config[ch].schedule.count == 0) continue;

        /* Check if it's time for the next entry */
        if ((int32_t)(now - s_schedule_state[ch].next_tick) < 0) continue;

        /* Check if channel is busy */
        if (sja1124_channel_busy(&s_sja_ctx, ch)) continue;

        /* Get the current schedule entry */
        uint8_t idx = s_schedule_state[ch].current_index;
        const lin_schedule_entry_t *entry = &s_channel_config[ch].schedule.entries[idx];

        /* Execute the entry */
        if (entry->dir == 1) {
            /* Master publish — transmit header + data */
            lin_frame_t frame;
            frame.id = entry->id;
            frame.dlc = entry->dlc;
            frame.classic_cs = entry->classic_cs;
            memcpy(frame.data, entry->data, entry->dlc);
            sja1124_frame_tx(&s_sja_ctx, ch, &frame);
        } else {
            /* Master subscribe — send header only, expect slave response */
            sja1124_header_tx(&s_sja_ctx, ch, entry->id, entry->dlc, entry->classic_cs);
        }

        /* Advance to next entry */
        idx = (idx + 1) % s_channel_config[ch].schedule.count;
        s_schedule_state[ch].current_index = idx;

        /* Schedule next tick based on the *next* entry's delay */
        const lin_schedule_entry_t *next_entry = &s_channel_config[ch].schedule.entries[idx];
        s_schedule_state[ch].next_tick = now + pdMS_TO_TICKS(next_entry->delay_ms);
    }
}

/* ---- Interrupt Processing ---- */

static void process_channel_interrupt(uint8_t ch)
{
    uint8_t ls, les;

    /* Read status register */
    if (sja1124_read_status(&s_sja_ctx, ch, &ls) != SJA_OK) return;

    /* Check for data reception complete */
    if (ls & SJA_LS_DRF) {
        lin_frame_t frame;
        if (sja1124_frame_rx(&s_sja_ctx, ch, &frame) == SJA_OK) {
            s_channel_stats[ch].rx_count++;

            /* Forward to gateway as a gateway_frame_t */
            gateway_frame_t gf;
            gf.source_bus = (bus_id_t)(BUS_LIN1 + ch);
            gf.timestamp = xTaskGetTickCount();
            gf.frame.id = frame.id;
            gf.frame.dlc = frame.dlc;
            gf.frame.flags = 0;
            memcpy(gf.frame.data, frame.data, frame.dlc);

            xQueueSend(s_gateway_queue, &gf, 0);
        }
    }

    /* Check for data transmission complete */
    if (ls & SJA_LS_DTF) {
        s_channel_stats[ch].tx_count++;
        sja1124_clear_status(&s_sja_ctx, ch, SJA_LS_DTF);
    }

    /* Check for errors */
    if (sja1124_read_error_status(&s_sja_ctx, ch, &les) == SJA_OK) {
        if (les & SJA_LES_ALL) {
            s_channel_stats[ch].error_count++;
            if (les & SJA_LES_TOF) {
                s_channel_stats[ch].timeout_count++;
            }
            sja1124_clear_errors(&s_sja_ctx, ch, les);
        }
    }
}

static void process_sja1124_interrupts(void)
{
    /* Process INT2: over-temperature and PLL loss-of-lock */
    uint8_t int2;
    if (sja1124_read_int2(&s_sja_ctx, &int2) == SJA_OK) {
        if (int2 & SJA_INT2_OTWI) {
            s_temp_warning_count++;
        }
        if (int2 & SJA_INT2_PLLOLI) {
            s_pll_lost_lock = true;
        }
    }

    /* Process INT3: per-channel status and errors */
    uint8_t int3;
    if (sja1124_read_int3(&s_sja_ctx, &int3) != SJA_OK) return;

    for (uint8_t ch = 0; ch < LIN_CHANNEL_COUNT; ch++) {
        if ((int3 & SJA_INT3_STS_MASK(ch)) || (int3 & SJA_INT3_ERR_MASK(ch))) {
            process_channel_interrupt(ch);
        }
    }
}

/* ---- Public API ---- */

void lin_manager_init(QueueHandle_t gateway_queue, QueueHandle_t lin_tx_queue)
{
    s_gateway_queue = gateway_queue;
    s_lin_tx_queue  = lin_tx_queue;

    memset(s_channel_config, 0, sizeof(s_channel_config));
    memset(s_channel_stats, 0, sizeof(s_channel_stats));
    memset(s_schedule_state, 0, sizeof(s_schedule_state));

    /* Initialize 8 MHz clock for SJA1124 PLL */
    hal_clock_init();

    /* Initialize SPI */
    hal_spi_init(&s_spi_ctx);

    /* NOTE: sja1124_init() is deferred to lin_task_entry() because it uses
     * vTaskDelay() and xTaskGetTickCount() which require the scheduler. */

    /* Setup GPIO interrupt on INTN pin (falling edge) */
    gpio_set_irq_enabled_with_callback(LIN_INT_PIN, GPIO_IRQ_EDGE_FALL,
                                        true, lin_int_gpio_callback);
}

bool lin_manager_start_channel(uint8_t ch, const lin_channel_config_t *config)
{
    if (ch >= LIN_CHANNEL_COUNT) return false;

    s_channel_config[ch] = *config;
    s_channel_stats[ch].state = LIN_STATE_INIT;

    sja1124_err_t err = sja1124_channel_init(&s_sja_ctx, ch, config);
    if (err != SJA_OK) {
        s_channel_stats[ch].state = LIN_STATE_ERROR;
        return false;
    }

    s_channel_stats[ch].state = LIN_STATE_ACTIVE;

    /* Start master scheduling if applicable */
    if (config->mode == LIN_MODE_MASTER && config->schedule.count > 0) {
        s_schedule_state[ch].active = true;
        s_schedule_state[ch].current_index = 0;
        s_schedule_state[ch].next_tick = xTaskGetTickCount() +
            pdMS_TO_TICKS(config->schedule.entries[0].delay_ms);
    }

    return true;
}

void lin_manager_stop_channel(uint8_t ch)
{
    if (ch >= LIN_CHANNEL_COUNT) return;

    s_schedule_state[ch].active = false;
    sja1124_channel_stop(&s_sja_ctx, ch);
    s_channel_config[ch].enabled = false;
    s_channel_stats[ch].state = LIN_STATE_UNINIT;
}

bool lin_manager_set_schedule(uint8_t ch, const lin_schedule_table_t *table)
{
    if (ch >= LIN_CHANNEL_COUNT) return false;
    if (s_channel_config[ch].mode != LIN_MODE_MASTER) return false;

    s_schedule_state[ch].active = false;
    s_channel_config[ch].schedule = *table;

    if (table->count > 0) {
        s_schedule_state[ch].current_index = 0;
        s_schedule_state[ch].next_tick = xTaskGetTickCount() +
            pdMS_TO_TICKS(table->entries[0].delay_ms);
        s_schedule_state[ch].active = true;
    }

    return true;
}

bool lin_manager_transmit(uint8_t ch, const lin_frame_t *frame)
{
    if (ch >= LIN_CHANNEL_COUNT) return false;
    return (sja1124_frame_tx(&s_sja_ctx, ch, frame) == SJA_OK);
}

void lin_manager_get_stats(uint8_t ch, lin_channel_stats_t *stats)
{
    if (ch >= LIN_CHANNEL_COUNT) return;
    *stats = s_channel_stats[ch];
}

uint32_t lin_manager_get_temp_warnings(void)
{
    return s_temp_warning_count;
}

bool lin_manager_get_pll_lost_lock(void)
{
    return s_pll_lost_lock;
}

/* ---- LIN Task ---- */

void lin_task_entry(void *params)
{
    (void)params;

    s_lin_task_handle = xTaskGetCurrentTaskHandle();

    /* Initialize SJA1124 now that the scheduler is running.
     * sja1124_init() uses vTaskDelay() for PLL lock polling,
     * which requires task context. */
    sja1124_init(&s_sja_ctx, &s_spi_ctx);

    for (;;) {
        /* Wait for INTN interrupt notification or 5 ms timeout */
        ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(5));

        /* Process SJA1124 interrupts */
        if (hal_lin_int_active()) {
            process_sja1124_interrupts();
        }

        /* Process outbound LIN TX queue */
        gateway_frame_t tx_gf;
        while (xQueueReceive(s_lin_tx_queue, &tx_gf, 0) == pdTRUE) {
            /* Determine target LIN channel from source_bus field */
            if (tx_gf.source_bus >= BUS_LIN1 && tx_gf.source_bus <= BUS_LIN4) {
                uint8_t ch = tx_gf.source_bus - BUS_LIN1;
                lin_frame_t frame;
                frame.id = tx_gf.frame.id & 0x3F;
                frame.dlc = tx_gf.frame.dlc;
                frame.classic_cs = false;
                memcpy(frame.data, tx_gf.frame.data, frame.dlc);
                lin_manager_transmit(ch, &frame);
            }
        }

        /* Run master scheduling engine */
        schedule_tick();
    }
}
