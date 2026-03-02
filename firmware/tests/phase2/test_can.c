/*
 * Phase 2 On-Target Test Firmware: CAN Subsystem
 * ================================================
 *
 * Build with -DTEST_PHASE2 to compile as standalone test firmware.
 *
 * This firmware:
 *   - Starts CAN1 and CAN2 at 500 kbps
 *   - Echoes any frame received on CAN1 back on CAN1 with ID | 0x100
 *   - Echoes any frame received on CAN2 back on CAN2 with ID | 0x100
 *   - Responds to special test command frames on CAN1 ID 0x7F0
 *   - Reports statistics on demand via CAN1 ID 0x7F1
 *
 * Test command frame (CAN1, ID 0x7F0):
 *   Byte 0: Command
 *     0x01 = Report stats (response on 0x7F1)
 *     0x02 = Enable CAN2
 *     0x03 = Disable CAN2
 *     0x04 = Re-enable CAN2
 *     0x05 = Change CAN1 bitrate (bytes 1-4 = bitrate LE)
 *     0x06 = Loopback test: send frame on CAN2 (bytes 1-4 = ID, byte 5 = DLC)
 *     0xFF = Report and halt
 *
 * Stats response (CAN1, ID 0x7F1):
 *   Byte 0: CAN1 RX count (low byte)
 *   Byte 1: CAN1 TX count (low byte)
 *   Byte 2: CAN1 error count (low byte)
 *   Byte 3: CAN1 overflow count (low byte)
 *   Byte 4: CAN2 RX count (low byte)
 *   Byte 5: CAN2 TX count (low byte)
 *   Byte 6: CAN2 state (enum value)
 *   Byte 7: CAN2 error count (low byte)
 */

#ifdef TEST_PHASE2

#include "pico/stdlib.h"
#include "FreeRTOS.h"
#include "task.h"
#include "queue.h"

#include "board_config.h"
#include "app_header.h"
#include "hal/hal_gpio.h"
#include "can/can_bus.h"
#include "can/can_manager.h"

#include <string.h>

#define TEST_CMD_ID     0x7F0
#define TEST_STATS_ID   0x7F1
#define ECHO_ID_OFFSET  0x100

static QueueHandle_t s_gw_queue;
static QueueHandle_t s_cfg_queue;
static QueueHandle_t s_tx_queue;

static void send_stats(void)
{
    can_bus_stats_t stats1, stats2;
    can_manager_get_stats(CAN_BUS_1, &stats1);
    can_manager_get_stats(CAN_BUS_2, &stats2);

    can_frame_t resp = {0};
    resp.id = TEST_STATS_ID;
    resp.dlc = 8;
    resp.data[0] = (uint8_t)(stats1.rx_count & 0xFF);
    resp.data[1] = (uint8_t)(stats1.tx_count & 0xFF);
    resp.data[2] = (uint8_t)(stats1.error_count & 0xFF);
    resp.data[3] = (uint8_t)(stats1.ring_overflow_count & 0xFF);
    resp.data[4] = (uint8_t)(stats2.rx_count & 0xFF);
    resp.data[5] = (uint8_t)(stats2.tx_count & 0xFF);
    resp.data[6] = (uint8_t)stats2.state;
    resp.data[7] = (uint8_t)(stats2.error_count & 0xFF);

    can_manager_transmit(CAN_BUS_1, &resp);
}

static void handle_test_cmd(const can_frame_t *frame)
{
    uint8_t cmd = frame->data[0];

    switch (cmd) {
    case 0x01: /* Report stats */
        send_stats();
        break;

    case 0x02: /* Enable CAN2 */
        can_manager_start_can2(CAN_DEFAULT_BITRATE);
        send_stats();
        break;

    case 0x03: /* Disable CAN2 */
        can_manager_stop_can2();
        send_stats();
        break;

    case 0x04: /* Re-enable CAN2 */
        can_manager_start_can2(CAN_DEFAULT_BITRATE);
        send_stats();
        break;

    case 0x05: { /* Change CAN1 bitrate */
        uint32_t bitrate = frame->data[1] |
                           (frame->data[2] << 8) |
                           (frame->data[3] << 16) |
                           (frame->data[4] << 24);
        can_manager_start_can1(bitrate);

        /* Echo back an ACK at the new bitrate */
        can_frame_t ack = {0};
        ack.id = TEST_STATS_ID;
        ack.dlc = 4;
        ack.data[0] = 0x05;
        ack.data[1] = frame->data[1];
        ack.data[2] = frame->data[2];
        ack.data[3] = frame->data[3];
        can_manager_transmit(CAN_BUS_1, &ack);
        break;
    }

    case 0x06: { /* Send frame on CAN2 */
        uint32_t id = frame->data[1] |
                      (frame->data[2] << 8) |
                      (frame->data[3] << 16) |
                      (frame->data[4] << 24);
        uint8_t dlc = frame->data[5];
        if (dlc > 8) dlc = 8;

        can_frame_t tx = {0};
        tx.id = id;
        tx.dlc = dlc;
        /* Fill with incrementing pattern */
        for (uint8_t i = 0; i < dlc; i++) tx.data[i] = i + 1;
        can_manager_transmit(CAN_BUS_2, &tx);
        break;
    }

    case 0xFF: /* Report and halt */
        send_stats();
        break;
    }
}

static uint32_t s_heartbeat_counter;
static uint32_t s_echo_count;

static void test_echo_task(void *params)
{
    (void)params;

    /* Startup diagnostic — confirms firmware is alive and CAN TX works */
    vTaskDelay(pdMS_TO_TICKS(500));
    {
        can_frame_t diag = {0};
        diag.id = 0x7FD;
        diag.dlc = 4;
        diag.data[0] = 'P';
        diag.data[1] = '2';
        diag.data[2] = 0x01;  /* Version */
        diag.data[3] = 0x00;
        can_manager_transmit(CAN_BUS_1, &diag);
        vTaskDelay(pdMS_TO_TICKS(50));
    }

    for (;;) {
        gateway_frame_t gf;

        /* Process gateway queue — test commands or echo */
        if (xQueueReceive(s_gw_queue, &gf, pdMS_TO_TICKS(5)) == pdTRUE) {
            if (gf.source_bus == BUS_CAN1 && gf.frame.id == TEST_CMD_ID) {
                /* Test command on 0x7F0 — handle instead of echo */
                handle_test_cmd(&gf.frame);
            } else {
                /* Echo with ID + 0x100 */
                can_frame_t echo = gf.frame;
                echo.id = gf.frame.id + ECHO_ID_OFFSET;

                if (gf.source_bus == BUS_CAN1) {
                    can_manager_transmit(CAN_BUS_1, &echo);
                    s_echo_count++;
                } else if (gf.source_bus == BUS_CAN2) {
                    can_manager_transmit(CAN_BUS_2, &echo);
                    s_echo_count++;
                }
            }
        }

        /* Process config queue — frames on 0x600/0x602 end up here.
         * For Phase 2, we don't handle them (T2.8 verifies they are
         * NOT echoed, confirming correct routing to config queue). */
        gateway_frame_t cfg_gf;
        while (xQueueReceive(s_cfg_queue, &cfg_gf, 0) == pdTRUE) {
            /* Drain but discard — config protocol not implemented yet */
        }

        /* Heartbeat every ~2 seconds (5ms poll × 400) with RX diagnostics */
        if (++s_heartbeat_counter >= 400) {
            s_heartbeat_counter = 0;
            can_bus_stats_t st;
            can_manager_get_stats(CAN_BUS_1, &st);
            can_frame_t hb = {0};
            hb.id = 0x7FC;
            hb.dlc = 8;
            hb.data[0] = (uint8_t)(xTaskGetTickCount() / 1000); /* uptime s */
            hb.data[1] = (uint8_t)st.isr_rx_count;  /* ISR RX callback */
            hb.data[2] = (uint8_t)st.rx_count;       /* can_task ring pop */
            hb.data[3] = (uint8_t)st.tx_count;       /* ISR TX callback */
            hb.data[4] = (uint8_t)st.error_count;    /* ISR error */
            hb.data[5] = (uint8_t)st.ring_overflow_count;
            hb.data[6] = (uint8_t)s_echo_count;      /* echoes sent */
            hb.data[7] = (uint8_t)st.state;
            can_manager_transmit(CAN_BUS_1, &hb);
        }
    }
}

/* ---- FreeRTOS hooks ---- */
void vApplicationIdleHook(void) {}
void vApplicationStackOverflowHook(TaskHandle_t t, char *n) { (void)t; (void)n; for(;;); }
void vApplicationMallocFailedHook(void) { for(;;); }

int main(void)
{
    stdio_init_all();
    hal_gpio_init();

    /* Enable CAN1 termination for proper bus signalling */
    hal_can_set_termination(CAN_BUS_1, true);

    s_gw_queue  = xQueueCreate(QUEUE_DEPTH_GATEWAY_IN, sizeof(gateway_frame_t));
    s_cfg_queue = xQueueCreate(QUEUE_DEPTH_CONFIG_RX,   sizeof(gateway_frame_t));
    s_tx_queue  = xQueueCreate(QUEUE_DEPTH_CAN_TX,      sizeof(gateway_frame_t));

    can_manager_init(s_gw_queue, s_cfg_queue, s_tx_queue);

    /* Start CAN1 (always on) */
    can_manager_start_can1(CAN_DEFAULT_BITRATE);

    /* CAN2 starts disabled — host test will enable it */

    xTaskCreate(can_task_entry,   "CAN",  TASK_STACK_CAN,  NULL, TASK_PRIORITY_CAN, NULL);
    xTaskCreate(test_echo_task,   "TEST", 1024,            NULL, 3,                 NULL);

    vTaskStartScheduler();
    for (;;) {}
    return 0;
}

#endif /* TEST_PHASE2 */
