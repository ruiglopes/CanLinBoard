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
#include "diag/diagnostics.h"
#include "config/config_handler.h"

/* ---- FreeRTOS Queue Handles (global) ---- */
QueueHandle_t g_gateway_input_queue;
QueueHandle_t g_can_tx_queue;
QueueHandle_t g_lin_tx_queue;
QueueHandle_t g_config_rx_queue;

/* ---- Task Handles (for stack watermark monitoring) ---- */
#define NUM_APP_TASKS   5
static TaskHandle_t s_task_handles[NUM_APP_TASKS];

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
        reset_reason_t reason;
        const crash_data_t *cd = fault_handler_get_crash_data();
        if (cd->magic == CRASH_DATA_MAGIC) {
            reason = RESET_CRASH_REBOOT;
        } else if (watchdog_enable_caused_reboot()) {
            reason = RESET_WATCHDOG_TIMEOUT;
        } else if (watchdog_caused_reboot()) {
            reason = RESET_UNKNOWN;
        } else {
            reason = RESET_POWER_ON;
        }
        diagnostics_set_reset_reason(reason);
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

    /* Initialize diagnostics module (before tasks, so handles are available) */
    diagnostics_init(s_task_handles, NUM_APP_TASKS);

    /* Create tasks — store handles for stack watermark monitoring */
    xTaskCreate(can_task_entry,      "CAN",  TASK_STACK_CAN,     NULL, TASK_PRIORITY_CAN,     &s_task_handles[0]);
    xTaskCreate(lin_task_entry,      "LIN",  TASK_STACK_LIN,     NULL, TASK_PRIORITY_LIN,     &s_task_handles[1]);
    xTaskCreate(gateway_task,        "GW",   TASK_STACK_GATEWAY,  NULL, TASK_PRIORITY_GATEWAY, &s_task_handles[2]);
    xTaskCreate(config_task,         "CFG",  TASK_STACK_CONFIG,   NULL, TASK_PRIORITY_CONFIG,  &s_task_handles[3]);
    xTaskCreate(diagnostics_task,    "DIAG", TASK_STACK_DIAG,     NULL, TASK_PRIORITY_DIAG,    &s_task_handles[4]);

    /* Enable hardware watchdog (5 second timeout, pause on debug) */
    watchdog_enable(HW_WATCHDOG_TIMEOUT_MS, true);

    vTaskStartScheduler();

    for (;;) { __breakpoint(); }
    return 0;
}
