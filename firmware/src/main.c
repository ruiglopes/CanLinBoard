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

    /* Startup diagnostic — confirm app is alive on the bus */
    vTaskDelay(pdMS_TO_TICKS(500));
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

    uint32_t uptime_s = 0;
    for (;;) {
        vTaskDelay(pdMS_TO_TICKS(1000));
        uptime_s++;

        can_frame_t hb = {0};
        hb.id = DIAG_DEFAULT_CAN_ID;
        hb.dlc = 8;

        /* Bytes 0-3: uptime in seconds (big-endian) */
        hb.data[0] = (uint8_t)(uptime_s >> 24);
        hb.data[1] = (uint8_t)(uptime_s >> 16);
        hb.data[2] = (uint8_t)(uptime_s >> 8);
        hb.data[3] = (uint8_t)(uptime_s);

        /* Bytes 4-5: CAN1 RX count (lower 16 bits) */
        can_bus_stats_t stats;
        can_manager_get_stats(CAN_BUS_1, &stats);
        hb.data[4] = (uint8_t)(stats.rx_count >> 8);
        hb.data[5] = (uint8_t)(stats.rx_count);

        /* Byte 6: CAN1 error count (saturate at 255) */
        hb.data[6] = (stats.error_count > 255) ? 255 : (uint8_t)stats.error_count;

        /* Byte 7: state flags — CAN1 active | CAN2 active | SJA1124 init */
        hb.data[7] = (stats.state == CAN_STATE_ACTIVE) ? 0x01 : 0x00;
        can_bus_stats_t stats2;
        can_manager_get_stats(CAN_BUS_2, &stats2);
        if (stats2.state == CAN_STATE_ACTIVE) hb.data[7] |= 0x02;

        can_manager_transmit(CAN_BUS_1, &hb);
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

#ifndef NO_BOOTLOADER
    /* Verify app header (only when running behind bootloader) */
    if (app_header.magic != APP_HEADER_MAGIC) {
        watchdog_reboot(0, 0, 0);
        for (;;) { __breakpoint(); }
    }
#endif

    /* Initialize HAL */
    hal_gpio_init();
    hal_can_set_termination(CAN_BUS_1, true);

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
