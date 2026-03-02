#include <stdio.h>
#include "pico/stdlib.h"
#include "hardware/watchdog.h"

#include "FreeRTOS.h"
#include "task.h"
#include "queue.h"
#include "semphr.h"

#include "board_config.h"
#include "app_header.h"
#include "hal/hal_gpio.h"
#include "can/can_bus.h"
#include "can/can_manager.h"
#include "lin/lin_manager.h"

/* ---- FreeRTOS Queue Handles (global) ---- */
QueueHandle_t g_gateway_input_queue;
QueueHandle_t g_can_tx_queue;
QueueHandle_t g_lin_tx_queue;
QueueHandle_t g_config_rx_queue;

/* ---- Stub tasks for phases not yet implemented ---- */

static void gateway_task(void *params)
{
    (void)params;
    for (;;) {
        /* Will be replaced by gateway_engine in Phase 4 */
        gateway_frame_t gf;
        xQueueReceive(g_gateway_input_queue, &gf, portMAX_DELAY);
        /* Drop frame — no routing rules yet */
    }
}

static void config_task(void *params)
{
    (void)params;
    for (;;) {
        /* Will be replaced by config_handler in Phase 5 */
        gateway_frame_t gf;
        xQueueReceive(g_config_rx_queue, &gf, portMAX_DELAY);
        /* Drop frame — no config handler yet */
    }
}

static void diag_task(void *params)
{
    (void)params;
    for (;;) {
        /* Will be replaced by diagnostics in Phase 6 */
        vTaskDelay(pdMS_TO_TICKS(100));
    }
}

/* ---- FreeRTOS Hooks ---- */

void vApplicationIdleHook(void)
{
}

void vApplicationStackOverflowHook(TaskHandle_t xTask, char *pcTaskName)
{
    (void)xTask;
    (void)pcTaskName;
    watchdog_reboot(0, 0, 0);
    for (;;) { __breakpoint(); }
}

void vApplicationMallocFailedHook(void)
{
    watchdog_reboot(0, 0, 0);
    for (;;) { __breakpoint(); }
}

/* ---- Main ---- */

int main(void)
{
    stdio_init_all();

    /* Verify app header */
    if (app_header.magic != APP_HEADER_MAGIC) {
        watchdog_reboot(0, 0, 0);
        for (;;) { __breakpoint(); }
    }

    /* Initialize HAL */
    hal_gpio_init();

    /* Create queues (using real gateway_frame_t size) */
    g_gateway_input_queue = xQueueCreate(QUEUE_DEPTH_GATEWAY_IN, sizeof(gateway_frame_t));
    g_can_tx_queue        = xQueueCreate(QUEUE_DEPTH_CAN_TX,     sizeof(gateway_frame_t));
    g_lin_tx_queue        = xQueueCreate(QUEUE_DEPTH_LIN_TX,     sizeof(gateway_frame_t));
    g_config_rx_queue     = xQueueCreate(QUEUE_DEPTH_CONFIG_RX,  sizeof(gateway_frame_t));

    /* Initialize subsystems */
    can_manager_init(g_gateway_input_queue, g_config_rx_queue, g_can_tx_queue);
    lin_manager_init(g_gateway_input_queue, g_lin_tx_queue);

    /* Start CAN1 (always on) */
    can_manager_start_can1(CAN_DEFAULT_BITRATE);

    /* Create tasks */
    xTaskCreate(can_task_entry,  "CAN",  TASK_STACK_CAN,     NULL, TASK_PRIORITY_CAN,     NULL);
    xTaskCreate(lin_task_entry,  "LIN",  TASK_STACK_LIN,     NULL, TASK_PRIORITY_LIN,     NULL);
    xTaskCreate(gateway_task,    "GW",   TASK_STACK_GATEWAY,  NULL, TASK_PRIORITY_GATEWAY, NULL);
    xTaskCreate(config_task,     "CFG",  TASK_STACK_CONFIG,   NULL, TASK_PRIORITY_CONFIG,  NULL);
    xTaskCreate(diag_task,       "DIAG", TASK_STACK_DIAG,     NULL, TASK_PRIORITY_DIAG,    NULL);

    vTaskStartScheduler();

    for (;;) { __breakpoint(); }
    return 0;
}
