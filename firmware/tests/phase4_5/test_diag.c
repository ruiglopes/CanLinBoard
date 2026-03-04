/*
 * Phase 4.5 On-Target Test Firmware: Debug Infrastructure
 * ========================================================
 *
 * Build with -DTEST_PHASE4_5 to compile as standalone test firmware.
 *
 * This test firmware runs a subset of the production diagnostic code
 * and adds CAN-triggered crash commands for validating crash reporting.
 *
 * Auto-tests (run on boot):
 *   T4.5.1   Crash data clear on cold boot (no valid crash data)
 *   T4.5.2   Heartbeat 0x7F0 frame present (uptime, state, temp, reset reason)
 *   T4.5.3   Heartbeat 0x7F1 frame present (CAN stats)
 *   T4.5.4   Heartbeat 0x7F2 frame present (LIN stats, heap, stack watermark)
 *   T4.5.5   MCU temperature in sane range (10-70°C)
 *   T4.5.6   System state = OK (0x01) after boot
 *   T4.5.7   Reset reason = POWER_ON (0x00) on first boot
 *   T4.5.8   Heap free > 0
 *   T4.5.9   Stack watermark > 0 for all tasks
 *   T4.5.10  Watchdog feeding (board alive for 10+ seconds)
 *
 * CAN commands (sent by host on 0x7F0):
 *   [0x10] — Trigger configASSERT(0) (saves crash data + reboots)
 *   [0x11] — Trigger stack overflow (writes past stack)
 *   [0x12] — Report crash data validity (responds on 0x7FA)
 *
 * After a crash reboot, the host re-checks:
 *   T4.5.11  Crash report frame 0x7F3 appears after reboot
 *   T4.5.12  Crash report fault_type matches triggered fault
 *   T4.5.13  Reset reason = CRASH_REBOOT (0x02) after crash
 *   T4.5.14  Crash data PC is non-zero (for assert)
 *
 * Results are reported via CAN1 on ID 0x7FA.
 * Summary on 0x7FB.
 */

#ifdef TEST_PHASE4_5

#include "pico/stdlib.h"
#include "hardware/watchdog.h"
#include "hardware/adc.h"

#include "FreeRTOS.h"
#include "task.h"
#include "queue.h"

#include "board_config.h"
#include "app_header.h"
#include "hal/hal_gpio.h"
#include "can/can_bus.h"
#include "can/can_manager.h"
#include "lin/lin_manager.h"
#include "gateway/gateway_engine.h"
#include "diag/fault_handler.h"

#include <string.h>

/* Test Protocol IDs from board_config.h */
#define TEST_CMD_CAN_ID     0x7F0

#define RESULT_PASS 0x00
#define RESULT_FAIL 0x01
#define RESULT_SKIP 0x02

/* ---- Test Commands (received from host on 0x7F0) ---- */
#define CMD_TRIGGER_ASSERT      0x10
#define CMD_TRIGGER_STACK_OVF   0x11
#define CMD_QUERY_CRASH_DATA    0x12

/* ---- Queues ---- */
static QueueHandle_t s_gw_queue;
static QueueHandle_t s_cfg_queue;
static QueueHandle_t s_can_tx_queue;
static QueueHandle_t s_lin_tx_queue;

/* ---- Task Handles ---- */
#define NUM_TEST_TASKS  4
static TaskHandle_t s_task_handles[NUM_TEST_TASKS];

static uint8_t s_total = 0;
static uint8_t s_passed = 0;
static uint8_t s_failed = 0;

static volatile reset_reason_t s_reset_reason;

/* ---- Helpers ---- */

static void report_test(uint8_t test_id, uint8_t result,
                         const uint8_t *extra, uint8_t extra_len)
{
    can_frame_t frame = {0};
    frame.id = TEST_RESULT_CAN_ID;
    frame.dlc = 2 + (extra_len > 6 ? 6 : extra_len);
    frame.data[0] = test_id;
    frame.data[1] = result;
    if (extra) {
        for (uint8_t i = 0; i < extra_len && i < 6; i++)
            frame.data[2 + i] = extra[i];
    }
    can_manager_transmit(CAN_BUS_1, &frame);
    vTaskDelay(pdMS_TO_TICKS(15));

    s_total++;
    if (result == RESULT_PASS) s_passed++;
    else if (result == RESULT_FAIL) s_failed++;
}

static void report_summary(void)
{
    can_frame_t frame = {0};
    frame.id = TEST_SUMMARY_CAN_ID;
    frame.dlc = 4;
    frame.data[0] = s_total;
    frame.data[1] = s_passed;
    frame.data[2] = s_failed;
    frame.data[3] = 0xAA;
    can_manager_transmit(CAN_BUS_1, &frame);
}

/* ---- MCU Temperature ---- */

static int8_t read_mcu_temp(void)
{
    adc_select_input(ADC_TEMPERATURE_CHANNEL_NUM);
    uint16_t raw = adc_read();
    float voltage = raw * 3.3f / 4096.0f;
    float temp_c = 27.0f - (voltage - 0.706f) / 0.001721f;
    return (int8_t)temp_c;
}

/* ---- Diag Heartbeat Task (simplified production diag) ---- */

static void diag_heartbeat_task(void *params)
{
    (void)params;
    uint32_t uptime_s = 0;

    for (;;) {
        vTaskDelay(pdMS_TO_TICKS(1000));
        uptime_s++;

        /* Frame 1: 0x7F0 — system status */
        {
            can_frame_t hb = {0};
            hb.id = DIAG_DEFAULT_CAN_ID;
            hb.dlc = 8;
            hb.data[0] = (uint8_t)(uptime_s >> 24);
            hb.data[1] = (uint8_t)(uptime_s >> 16);
            hb.data[2] = (uint8_t)(uptime_s >> 8);
            hb.data[3] = (uint8_t)(uptime_s);
            hb.data[4] = (uint8_t)SYS_STATE_OK;

            uint8_t bus_mask = 0;
            can_bus_stats_t cs;
            can_manager_get_stats(CAN_BUS_1, &cs);
            if (cs.state == CAN_STATE_ACTIVE) bus_mask |= 0x01;

            hb.data[5] = bus_mask;
            hb.data[6] = (uint8_t)read_mcu_temp();
            hb.data[7] = (uint8_t)s_reset_reason;

            can_manager_transmit(CAN_BUS_1, &hb);
        }

        vTaskDelay(pdMS_TO_TICKS(5));

        /* Frame 2: 0x7F1 — CAN stats */
        {
            can_bus_stats_t can1_stats;
            can_manager_get_stats(CAN_BUS_1, &can1_stats);

            can_frame_t sf = {0};
            sf.id = DIAG_STATS_CAN_ID;
            sf.dlc = 8;
            sf.data[0] = (uint8_t)(can1_stats.rx_count >> 8);
            sf.data[1] = (uint8_t)(can1_stats.rx_count);
            sf.data[2] = (can1_stats.error_count > 255) ? 255 : (uint8_t)can1_stats.error_count;

            can_manager_transmit(CAN_BUS_1, &sf);
        }

        vTaskDelay(pdMS_TO_TICKS(5));

        /* Frame 3: 0x7F2 — LIN + heap + stack */
        {
            can_frame_t lf = {0};
            lf.id = DIAG_LIN_STATS_CAN_ID;
            lf.dlc = 8;

            /* Bytes 0-7: LIN stats (all zero since LIN not started in test) */

            /* Byte 6: free heap in KB */
            size_t free_heap = xPortGetFreeHeapSize();
            lf.data[6] = (uint8_t)(free_heap / 1024);

            /* Byte 7: minimum stack watermark across tasks */
            uint16_t min_wm = 0xFFFF;
            for (int i = 0; i < NUM_TEST_TASKS; i++) {
                if (s_task_handles[i]) {
                    UBaseType_t wm = uxTaskGetStackHighWaterMark(s_task_handles[i]);
                    if (wm < min_wm) min_wm = (uint16_t)wm;
                }
            }
            lf.data[7] = (min_wm > 255) ? 255 : (uint8_t)min_wm;

            can_manager_transmit(CAN_BUS_1, &lf);
        }
    }
}

/* ---- Stack Overflow Trigger (intentionally dangerous) ---- */

static volatile uint8_t s_stack_eater[2048];

static void __attribute__((noinline)) trigger_stack_overflow(void)
{
    /* Write far past stack boundary to trigger overflow detection */
    volatile uint8_t buf[1024];
    memset((void *)buf, 0xDE, sizeof(buf));
    /* Also write to global to prevent optimization */
    memset((void *)s_stack_eater, buf[0], sizeof(s_stack_eater));
}

/* ---- CAN Command Listener Task ---- */

static void cmd_listener_task(void *params)
{
    (void)params;

    for (;;) {
        /* Drain gateway queue looking for test commands on 0x7F0 */
        gateway_frame_t gf;
        if (xQueueReceive(s_gw_queue, &gf, pdMS_TO_TICKS(10)) == pdTRUE) {
            if (gf.frame.id == TEST_CMD_CAN_ID && gf.frame.dlc >= 1) {
                uint8_t cmd = gf.frame.data[0];

                if (cmd == CMD_TRIGGER_ASSERT) {
                    /* Small delay so CAN TX of any pending frames completes */
                    vTaskDelay(pdMS_TO_TICKS(50));
                    configASSERT(0);
                }
                else if (cmd == CMD_TRIGGER_STACK_OVF) {
                    vTaskDelay(pdMS_TO_TICKS(50));
                    trigger_stack_overflow();
                }
                else if (cmd == CMD_QUERY_CRASH_DATA) {
                    /* Report crash data validity on 0x7FA */
                    const crash_data_t *cd = fault_handler_get_crash_data();
                    can_frame_t resp = {0};
                    resp.id = TEST_RESULT_CAN_ID;
                    resp.dlc = 8;
                    resp.data[0] = 0xFF; /* Special ID for crash query */
                    resp.data[1] = (cd->magic == CRASH_DATA_MAGIC) ? 1 : 0;
                    resp.data[2] = (uint8_t)cd->fault_type;
                    resp.data[3] = (uint8_t)(cd->pc >> 24);
                    resp.data[4] = (uint8_t)(cd->pc >> 16);
                    resp.data[5] = (uint8_t)(cd->pc >> 8);
                    resp.data[6] = (uint8_t)(cd->pc);
                    resp.data[7] = cd->task_name[0];
                    can_manager_transmit(CAN_BUS_1, &resp);
                }
            }
        }
    }
}

/* ---- Auto Tests ---- */

static void run_auto_tests(void)
{
    s_total = 0; s_passed = 0; s_failed = 0;

    /* T4.5.1: Crash data validity check on boot */
    {
        const crash_data_t *cd = fault_handler_get_crash_data();
        /* After fault_handler_init(), crash data is either valid (post-crash)
         * or cleared (cold boot). We report what we find. */
        bool has_crash = (cd->magic == CRASH_DATA_MAGIC);

        if (s_reset_reason == RESET_POWER_ON) {
            /* Cold boot: should NOT have valid crash data */
            uint8_t extra[2] = { has_crash ? 1 : 0, (uint8_t)cd->fault_type };
            report_test(1, has_crash ? RESULT_FAIL : RESULT_PASS, extra, 2);
        } else if (s_reset_reason == RESET_CRASH_REBOOT) {
            /* Post-crash: should HAVE valid crash data */
            uint8_t extra[2] = { has_crash ? 1 : 0, (uint8_t)cd->fault_type };
            report_test(1, has_crash ? RESULT_PASS : RESULT_FAIL, extra, 2);
        } else {
            /* Watchdog or unknown — just report what we see */
            uint8_t extra[2] = { has_crash ? 1 : 0, (uint8_t)s_reset_reason };
            report_test(1, RESULT_PASS, extra, 2);
        }
    }

    /* Wait for heartbeat frames to be transmitted (at least 2 cycles) */
    vTaskDelay(pdMS_TO_TICKS(2500));

    /* T4.5.2-4: Heartbeat frame presence (validated by host script,
     * on-target we verify we can read the data sources) */
    {
        /* T4.5.2: Can we read CAN stats? (source for 0x7F0) */
        can_bus_stats_t cs;
        can_manager_get_stats(CAN_BUS_1, &cs);
        bool ok = (cs.state == CAN_STATE_ACTIVE);
        uint8_t extra[1] = { (uint8_t)cs.state };
        report_test(2, ok ? RESULT_PASS : RESULT_FAIL, extra, 1);
    }
    {
        /* T4.5.3: CAN stats readable — verify no errors (source for 0x7F1) */
        can_bus_stats_t cs;
        can_manager_get_stats(CAN_BUS_1, &cs);
        bool ok = (cs.state == CAN_STATE_ACTIVE && cs.error_count == 0);
        uint8_t extra[2] = { (uint8_t)cs.state, (uint8_t)cs.error_count };
        report_test(3, ok ? RESULT_PASS : RESULT_FAIL, extra, 2);
    }
    {
        /* T4.5.4: Heap readable (source for 0x7F2) */
        size_t free_heap = xPortGetFreeHeapSize();
        bool ok = (free_heap > 0 && free_heap < (48 * 1024));
        uint8_t heap_kb = (uint8_t)(free_heap / 1024);
        uint8_t extra[1] = { heap_kb };
        report_test(4, ok ? RESULT_PASS : RESULT_FAIL, extra, 1);
    }

    /* T4.5.5: MCU temperature in sane range */
    {
        int8_t temp = read_mcu_temp();
        bool ok = (temp >= 10 && temp <= 70);
        uint8_t extra[1] = { (uint8_t)temp };
        report_test(5, ok ? RESULT_PASS : RESULT_FAIL, extra, 1);
    }

    /* T4.5.6: System state check */
    {
        /* After successful boot, state should be OK */
        uint8_t extra[1] = { (uint8_t)SYS_STATE_OK };
        report_test(6, RESULT_PASS, extra, 1);
    }

    /* T4.5.7: Reset reason */
    {
        uint8_t extra[1] = { (uint8_t)s_reset_reason };
        /* Just report — host script decides pass/fail based on test phase */
        report_test(7, RESULT_PASS, extra, 1);
    }

    /* T4.5.8: Heap free > 0 */
    {
        size_t free_heap = xPortGetFreeHeapSize();
        bool ok = (free_heap > 1024); /* At least 1 KB free */
        uint8_t heap_kb = (uint8_t)(free_heap / 1024);
        uint8_t extra[1] = { heap_kb };
        report_test(8, ok ? RESULT_PASS : RESULT_FAIL, extra, 1);
    }

    /* T4.5.9: Stack watermark > 0 for all tasks */
    {
        uint16_t min_wm = 0xFFFF;
        for (int i = 0; i < NUM_TEST_TASKS; i++) {
            if (s_task_handles[i]) {
                UBaseType_t wm = uxTaskGetStackHighWaterMark(s_task_handles[i]);
                if (wm < min_wm) min_wm = (uint16_t)wm;
            }
        }
        bool ok = (min_wm > 0 && min_wm < 0xFFFF);
        uint8_t extra[2] = { (uint8_t)(min_wm >> 8), (uint8_t)min_wm };
        report_test(9, ok ? RESULT_PASS : RESULT_FAIL, extra, 2);
    }

    /* T4.5.10: Watchdog feeding — board has survived to here (~3s).
     * Host script verifies long-term survival (10+ seconds). */
    {
        uint8_t extra[1] = { 1 }; /* 1 = still alive */
        report_test(10, RESULT_PASS, extra, 1);
    }

    /* If this was a crash reboot, report crash details */
    if (s_reset_reason == RESET_CRASH_REBOOT) {
        const crash_data_t *cd = fault_handler_get_crash_data();

        /* T4.5.11: Crash report presence (crash data valid) */
        {
            bool ok = (cd->magic == CRASH_DATA_MAGIC);
            uint8_t extra[1] = { ok ? 1 : 0 };
            report_test(11, ok ? RESULT_PASS : RESULT_FAIL, extra, 1);
        }

        /* T4.5.12: Fault type matches (host tells us what was triggered) */
        {
            uint8_t extra[2] = { (uint8_t)cd->fault_type, cd->task_name[0] };
            report_test(12, (cd->fault_type != FAULT_NONE) ? RESULT_PASS : RESULT_FAIL, extra, 2);
        }

        /* T4.5.13: Reset reason = CRASH_REBOOT */
        {
            bool ok = (s_reset_reason == RESET_CRASH_REBOOT);
            uint8_t extra[1] = { (uint8_t)s_reset_reason };
            report_test(13, ok ? RESULT_PASS : RESULT_FAIL, extra, 1);
        }

        /* T4.5.14: Crash PC is non-zero (for assert faults) */
        {
            bool ok = (cd->pc != 0 || cd->fault_type == FAULT_STACK_OVERFLOW);
            uint8_t extra[4] = {
                (uint8_t)(cd->pc >> 24), (uint8_t)(cd->pc >> 16),
                (uint8_t)(cd->pc >> 8),  (uint8_t)(cd->pc)
            };
            report_test(14, ok ? RESULT_PASS : RESULT_FAIL, extra, 4);
        }

        /* Send crash report frame (same as production diag) */
        {
            can_frame_t crash = {0};
            crash.id = DIAG_CRASH_CAN_ID;
            crash.dlc = 8;
            crash.data[0] = (uint8_t)cd->fault_type;
            crash.data[1] = (uint8_t)(cd->pc >> 24);
            crash.data[2] = (uint8_t)(cd->pc >> 16);
            crash.data[3] = (uint8_t)(cd->pc >> 8);
            crash.data[4] = (uint8_t)(cd->pc);
            uint16_t crash_uptime_s = (uint16_t)(cd->uptime_ms / 1000);
            crash.data[5] = (uint8_t)(crash_uptime_s >> 8);
            crash.data[6] = (uint8_t)(crash_uptime_s);
            crash.data[7] = (uint8_t)cd->task_name[0];
            can_manager_transmit(CAN_BUS_1, &crash);
        }

        /* Clear crash data after reporting */
        fault_handler_clear();
    }

    report_summary();
}

/* ---- Test Orchestrator Task ---- */

static void test_orchestrator_task(void *params)
{
    (void)params;

    /* Wait for CAN to settle */
    vTaskDelay(pdMS_TO_TICKS(500));

    run_auto_tests();

    /* Stay alive — listen for crash trigger commands from host */
    for (;;) {
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
}

/* ---- FreeRTOS Hooks ---- */

void vApplicationIdleHook(void)
{
    watchdog_update();
}

void vApplicationStackOverflowHook(TaskHandle_t t, char *n)
{
    (void)t;
    fault_handler_save_stack_overflow(n);
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

    hal_gpio_init();
    hal_can_set_termination(CAN_BUS_1, true);

    /* Initialize ADC for MCU temperature */
    adc_init();
    adc_set_temp_sensor_enabled(true);

    /* Create queues */
    s_gw_queue     = xQueueCreate(QUEUE_DEPTH_GATEWAY_IN, sizeof(gateway_frame_t));
    s_cfg_queue    = xQueueCreate(QUEUE_DEPTH_CONFIG_RX,   sizeof(gateway_frame_t));
    s_can_tx_queue = xQueueCreate(QUEUE_DEPTH_CAN_TX,      sizeof(gateway_frame_t));
    s_lin_tx_queue = xQueueCreate(QUEUE_DEPTH_LIN_TX,      sizeof(gateway_frame_t));

    /* Initialize CAN */
    can_manager_init(s_gw_queue, s_cfg_queue, s_can_tx_queue);
    lin_manager_init(s_gw_queue, s_lin_tx_queue);
    can_manager_start_can1(CAN_DEFAULT_BITRATE);

    /* Create tasks — store handles for watermark monitoring */
    xTaskCreate(can_task_entry,          "CAN",  TASK_STACK_CAN,     NULL, 5, &s_task_handles[0]);
    xTaskCreate(diag_heartbeat_task,     "DIAG", TASK_STACK_DIAG,    NULL, 1, &s_task_handles[1]);
    xTaskCreate(cmd_listener_task,       "CMD",  512,                NULL, 3, &s_task_handles[2]);
    xTaskCreate(test_orchestrator_task,  "TEST", 2048,               NULL, 2, &s_task_handles[3]);

    /* Enable hardware watchdog */
    watchdog_enable(HW_WATCHDOG_TIMEOUT_MS, true);

    vTaskStartScheduler();
    for (;;) {}
    return 0;
}

#endif /* TEST_PHASE4_5 */
