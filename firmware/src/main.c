#include <stdio.h>
#include "pico/stdlib.h"
#include "hardware/watchdog.h"
#include "hardware/adc.h"

#include "FreeRTOS.h"
#include "task.h"
#include "queue.h"
#include "semphr.h"

#include "board_config.h"
#include "app_header.h"
#include "hal/hal_gpio.h"
#include "hal/hal_flash_nvm.h"
#include "can/can_bus.h"
#include "can/can_manager.h"
#include "lin/lin_manager.h"
#include "gateway/gateway_engine.h"
#include "diag/fault_handler.h"
#include "config/config_handler.h"

/* ---- FreeRTOS Queue Handles (global) ---- */
QueueHandle_t g_gateway_input_queue;
QueueHandle_t g_can_tx_queue;
QueueHandle_t g_lin_tx_queue;
QueueHandle_t g_config_rx_queue;

/* ---- Task Handles (for stack watermark monitoring) ---- */
#define NUM_APP_TASKS   5
static TaskHandle_t s_task_handles[NUM_APP_TASKS];

/* ---- System State ---- */
static volatile system_state_t s_sys_state = SYS_STATE_BOOT;
static reset_reason_t s_reset_reason = RESET_POWER_ON;

/* ---- Gateway Task (Phase 4) ---- */

static void gateway_task(void *params)
{
    (void)params;
    gateway_engine_init(g_can_tx_queue, g_lin_tx_queue);
    for (;;) {
        gateway_frame_t gf;
        if (xQueueReceive(g_gateway_input_queue, &gf, portMAX_DELAY) == pdTRUE) {
            gateway_engine_process(&gf);
        }
    }
}

static void config_task(void *params)
{
    (void)params;
    config_handler_task(NULL);  /* Never returns */
}

/* ---- MCU Temperature Reading ---- */

static int8_t read_mcu_temp(void)
{
    adc_select_input(ADC_TEMPERATURE_CHANNEL_NUM);
    uint16_t raw = adc_read();
    float voltage = raw * 3.3f / 4096.0f;
    float temp_c = 27.0f - (voltage - 0.706f) / 0.001721f;
    return (int8_t)temp_c;
}

/* ---- Diagnostic Task ---- */

static void diag_task(void *params)
{
    (void)params;

    /* Wait for subsystems to initialize */
    vTaskDelay(pdMS_TO_TICKS(500));

    /* One-shot: version frame on 0x7F0 */
    {
        can_frame_t diag = {0};
        diag.id = DIAG_DEFAULT_CAN_ID;  /* 0x7F0 */
        diag.dlc = 6;
        diag.data[0] = (FW_VERSION_MAJOR);
        diag.data[1] = (FW_VERSION_MINOR);
        diag.data[2] = (FW_VERSION_PATCH);
        diag.data[3] = 0xAA;  /* "app running" sentinel */
#ifndef NO_BOOTLOADER
        diag.data[4] = (uint8_t)(app_header.crc32 >> 8);
        diag.data[5] = (uint8_t)(app_header.crc32);
#endif
        can_manager_transmit(CAN_BUS_1, &diag);
    }

    /* One-shot: crash report on 0x7F3 (if valid crash data exists) */
    {
        const crash_data_t *cd = fault_handler_get_crash_data();
        if (cd->magic == CRASH_DATA_MAGIC) {
            can_frame_t crash = {0};
            crash.id = DIAG_CRASH_CAN_ID;  /* 0x7F3 */
            crash.dlc = 8;
            crash.data[0] = (uint8_t)cd->fault_type;
            /* PC (big-endian, upper 3 bytes) */
            crash.data[1] = (uint8_t)(cd->pc >> 24);
            crash.data[2] = (uint8_t)(cd->pc >> 16);
            crash.data[3] = (uint8_t)(cd->pc >> 8);
            crash.data[4] = (uint8_t)(cd->pc);
            /* Uptime at crash (upper 2 bytes in seconds) */
            uint16_t crash_uptime_s = (uint16_t)(cd->uptime_ms / 1000);
            crash.data[5] = (uint8_t)(crash_uptime_s >> 8);
            crash.data[6] = (uint8_t)(crash_uptime_s);
            /* Task name first char */
            crash.data[7] = (uint8_t)cd->task_name[0];
            can_manager_transmit(CAN_BUS_1, &crash);

            /* Clear crash data after reporting */
            fault_handler_clear();
        }
    }

    s_sys_state = SYS_STATE_OK;
    uint32_t uptime_s = 0;
    uint32_t watermark_check_counter = 0;

    for (;;) {
        vTaskDelay(pdMS_TO_TICKS(1000));
        uptime_s++;
        watermark_check_counter++;

        /* ---- Frame 1: System status on 0x7F0 (8B) ---- */
        {
            can_frame_t hb = {0};
            hb.id = DIAG_DEFAULT_CAN_ID;
            hb.dlc = 8;

            /* Bytes 0-3: uptime in seconds (big-endian) */
            hb.data[0] = (uint8_t)(uptime_s >> 24);
            hb.data[1] = (uint8_t)(uptime_s >> 16);
            hb.data[2] = (uint8_t)(uptime_s >> 8);
            hb.data[3] = (uint8_t)(uptime_s);

            /* Byte 4: system state */
            hb.data[4] = (uint8_t)s_sys_state;

            /* Byte 5: bus active mask (bit0=CAN1, bit1=CAN2, bits2-5=LIN1-4) */
            uint8_t bus_mask = 0;
            can_bus_stats_t can1_stats;
            can_manager_get_stats(CAN_BUS_1, &can1_stats);
            if (can1_stats.state == CAN_STATE_ACTIVE) bus_mask |= 0x01;
            can_bus_stats_t can2_stats;
            can_manager_get_stats(CAN_BUS_2, &can2_stats);
            if (can2_stats.state == CAN_STATE_ACTIVE) bus_mask |= 0x02;
            for (uint8_t ch = 0; ch < LIN_CHANNEL_COUNT; ch++) {
                lin_channel_stats_t ls;
                lin_manager_get_stats(ch, &ls);
                if (ls.state == LIN_STATE_ACTIVE) bus_mask |= (0x04 << ch);
            }
            hb.data[5] = bus_mask;

            /* Byte 6: MCU temperature (signed °C) */
            hb.data[6] = (uint8_t)read_mcu_temp();

            /* Byte 7: reset reason */
            hb.data[7] = (uint8_t)s_reset_reason;

            can_manager_transmit(CAN_BUS_1, &hb);
        }

        /* Small stagger to avoid bus burst */
        vTaskDelay(pdMS_TO_TICKS(5));

        /* ---- Frame 2: CAN + Gateway stats on 0x7F1 (8B) ---- */
        {
            can_bus_stats_t can1_stats, can2_stats;
            can_manager_get_stats(CAN_BUS_1, &can1_stats);
            can_manager_get_stats(CAN_BUS_2, &can2_stats);

            gateway_stats_t gw_stats;
            gateway_engine_get_stats(&gw_stats);

            can_frame_t sf = {0};
            sf.id = DIAG_STATS_CAN_ID;
            sf.dlc = 8;

            /* CAN1 RX count (lower 16 bits) */
            sf.data[0] = (uint8_t)(can1_stats.rx_count >> 8);
            sf.data[1] = (uint8_t)(can1_stats.rx_count);
            /* CAN1 error count (saturate at 255) */
            sf.data[2] = (can1_stats.error_count > 255) ? 255 : (uint8_t)can1_stats.error_count;
            /* CAN2 RX count (lower 16 bits) */
            sf.data[3] = (uint8_t)(can2_stats.rx_count >> 8);
            sf.data[4] = (uint8_t)(can2_stats.rx_count);
            /* CAN2 error count (saturate at 255) */
            sf.data[5] = (can2_stats.error_count > 255) ? 255 : (uint8_t)can2_stats.error_count;
            /* Gateway routed count (lower 16 bits) */
            sf.data[6] = (uint8_t)(gw_stats.frames_routed >> 8);
            sf.data[7] = (uint8_t)(gw_stats.frames_routed);

            can_manager_transmit(CAN_BUS_1, &sf);
        }

        vTaskDelay(pdMS_TO_TICKS(5));

        /* ---- Frame 3: LIN + heap/stack on 0x7F2 (8B) ---- */
        {
            can_frame_t lf = {0};
            lf.id = DIAG_LIN_STATS_CAN_ID;
            lf.dlc = 8;

            for (uint8_t ch = 0; ch < LIN_CHANNEL_COUNT; ch++) {
                lin_channel_stats_t ls;
                lin_manager_get_stats(ch, &ls);
                /* 2 bytes per channel: rx_count(1B sat), error_count(1B sat) */
                lf.data[ch * 2]     = (ls.rx_count > 255) ? 255 : (uint8_t)ls.rx_count;
                lf.data[ch * 2 + 1] = (ls.error_count > 255) ? 255 : (uint8_t)ls.error_count;
            }

            /* Byte 6: free heap in KB */
            size_t free_heap = xPortGetFreeHeapSize();
            lf.data[6] = (uint8_t)(free_heap / 1024);

            /* Byte 7: minimum stack watermark across all tasks (in words) */
            uint16_t min_wm = 0xFFFF;
            for (int i = 0; i < NUM_APP_TASKS; i++) {
                if (s_task_handles[i]) {
                    UBaseType_t wm = uxTaskGetStackHighWaterMark(s_task_handles[i]);
                    if (wm < min_wm) min_wm = (uint16_t)wm;
                }
            }
            lf.data[7] = (min_wm > 255) ? 255 : (uint8_t)min_wm;

            can_manager_transmit(CAN_BUS_1, &lf);
        }

        /* Every 10 seconds: check stack watermarks for warnings */
        if (watermark_check_counter >= 10) {
            watermark_check_counter = 0;
            for (int i = 0; i < NUM_APP_TASKS; i++) {
                if (s_task_handles[i]) {
                    UBaseType_t wm = uxTaskGetStackHighWaterMark(s_task_handles[i]);
                    /* Get the stack size for this task — use a conservative threshold.
                     * If watermark < 25% of minimal usable stack (32 words), warn. */
                    if (wm < 32) {
                        s_sys_state = SYS_STATE_WARN;
                        break;
                    }
                }
            }
        }
    }
}

/* ---- FreeRTOS Hooks ---- */

void vApplicationIdleHook(void)
{
    watchdog_update();
}

void vApplicationStackOverflowHook(TaskHandle_t xTask, char *pcTaskName)
{
    (void)xTask;
    fault_handler_save_stack_overflow(pcTaskName);
}

void vApplicationMallocFailedHook(void)
{
    fault_handler_save_malloc_fail();
}

/* ---- Main ---- */

int main(void)
{
    stdio_init_all();

    /* Initialize fault handler (checks for crash data from previous boot) */
    fault_handler_init();

    /* Determine reset reason */
    {
        const crash_data_t *cd = fault_handler_get_crash_data();
        if (cd->magic == CRASH_DATA_MAGIC) {
            s_reset_reason = RESET_CRASH_REBOOT;
        } else if (watchdog_enable_caused_reboot()) {
            s_reset_reason = RESET_WATCHDOG_TIMEOUT;
        } else if (watchdog_caused_reboot()) {
            s_reset_reason = RESET_UNKNOWN;
        } else {
            s_reset_reason = RESET_POWER_ON;
        }
    }

#ifndef NO_BOOTLOADER
    /* Verify app header (only when running behind bootloader) */
    if (app_header.magic != APP_HEADER_MAGIC) {
        watchdog_reboot(0, 0, 0);
        for (;;) { __breakpoint(); }
    }
#endif

    /* Initialize HAL */
    hal_gpio_init();

    /* Initialize ADC for MCU temperature sensor */
    adc_init();
    adc_set_temp_sensor_enabled(true);

    /* Initialize NVM */
    hal_nvm_init();

    /* Create queues (using real gateway_frame_t size) */
    g_gateway_input_queue = xQueueCreate(QUEUE_DEPTH_GATEWAY_IN, sizeof(gateway_frame_t));
    g_can_tx_queue        = xQueueCreate(QUEUE_DEPTH_CAN_TX,     sizeof(gateway_frame_t));
    g_lin_tx_queue        = xQueueCreate(QUEUE_DEPTH_LIN_TX,     sizeof(gateway_frame_t));
    g_config_rx_queue     = xQueueCreate(QUEUE_DEPTH_CONFIG_RX,  sizeof(gateway_frame_t));

    /* Initialize config handler — loads config from NVM (or defaults) */
    config_handler_init(g_config_rx_queue, g_can_tx_queue);
    const nvm_config_t *cfg = config_handler_get_config();

    /* Initialize subsystems */
    can_manager_init(g_gateway_input_queue, g_config_rx_queue, g_can_tx_queue);
    lin_manager_init(g_gateway_input_queue, g_lin_tx_queue);

    /* Start CAN1 with config bitrate */
    can_manager_start_can1(cfg->can[0].bitrate);
    hal_can_set_termination(CAN_BUS_1, cfg->can[0].termination);

    /* Start CAN2 if enabled in config */
    if (cfg->can[1].enabled) {
        can_manager_start_can2(cfg->can[1].bitrate);
        hal_can_set_termination(CAN_BUS_2, cfg->can[1].termination);
    }

    /* Create tasks — store handles for stack watermark monitoring */
    xTaskCreate(can_task_entry,  "CAN",  TASK_STACK_CAN,     NULL, TASK_PRIORITY_CAN,     &s_task_handles[0]);
    xTaskCreate(lin_task_entry,  "LIN",  TASK_STACK_LIN,     NULL, TASK_PRIORITY_LIN,     &s_task_handles[1]);
    xTaskCreate(gateway_task,    "GW",   TASK_STACK_GATEWAY,  NULL, TASK_PRIORITY_GATEWAY, &s_task_handles[2]);
    xTaskCreate(config_task,     "CFG",  TASK_STACK_CONFIG,   NULL, TASK_PRIORITY_CONFIG,  &s_task_handles[3]);
    xTaskCreate(diag_task,       "DIAG", TASK_STACK_DIAG,     NULL, TASK_PRIORITY_DIAG,    &s_task_handles[4]);

    /* Enable hardware watchdog (5 second timeout, pause on debug) */
    watchdog_enable(HW_WATCHDOG_TIMEOUT_MS, true);

    vTaskStartScheduler();

    for (;;) { __breakpoint(); }
    return 0;
}
