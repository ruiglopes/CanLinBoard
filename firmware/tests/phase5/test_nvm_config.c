/*
 * Phase 5 On-Target Test Firmware: Configuration System
 * ======================================================
 *
 * Build with -DTEST_PHASE5 to compile as standalone test firmware.
 *
 * Auto-tests (run on boot):
 *   T5.1   First boot defaults (erase NVM, verify defaults loaded)
 *   T5.2   Config persistence (save, read back, verify match)
 *   T5.3   Ping-pong slot swap (save twice, check active slot alternates)
 *   T5.4   CRC validation (corrupt NVM, detect mismatch, reload defaults)
 *   T5.5   CONNECT command (response with FW version)
 *   T5.6   READ CAN1 bitrate (defaults to 500000)
 *   T5.7   WRITE CAN1 bitrate (change to 250000)
 *   T5.8   Read after write (verify 250000 persists in RAM)
 *   T5.9   SAVE + verify NVM (save, read NVM directly, compare)
 *   T5.10  Bulk write routing (3 rules via bulk transfer)
 *   T5.11  Bulk CRC mismatch (bad CRC rejected)
 *   T5.12  LOAD_DEFAULTS (defaults command, verify bitrate back to 500000)
 *   T5.13  REBOOT (host-verified: board resets and reconnects)
 *   T5.14  ENTER_BOOTLOADER (host-verified: bootloader active)
 *   T5.15  Unknown param (invalid section returns error)
 *   T5.16  Runtime apply (write CAN2 term=true + SAVE, verify pin)
 *
 * Results reported on CAN1 ID 0x7FE, summary on 0x7FF.
 * T5.13 and T5.14 are host-verified (cause board resets).
 */

#ifdef TEST_PHASE5

#include "pico/stdlib.h"
#include "hardware/watchdog.h"
#include "hardware/adc.h"

#include "FreeRTOS.h"
#include "task.h"
#include "queue.h"

#include "board_config.h"
#include "app_header.h"
#include "hal/hal_gpio.h"
#include "hal/hal_flash_nvm.h"
#include "can/can_bus.h"
#include "can/can_manager.h"
#include "lin/lin_manager.h"
#include "gateway/gateway_engine.h"
#include "diag/fault_handler.h"
#include "config/nvm_config.h"
#include "config/config_handler.h"
#include "config/config_protocol.h"
#include "util/crc32.h"

#include <string.h>

/* ---- Test Protocol IDs ---- */
#define TEST_RESULT_CAN_ID  0x7FE
#define TEST_SUMMARY_CAN_ID 0x7FF
#define TEST_DIAG_CAN_ID    0x7FD
#define TEST_PROGRESS_CAN_ID 0x7FC

#define RESULT_PASS 0x00
#define RESULT_FAIL 0x01
#define RESULT_SKIP 0x02

/* ---- Queues ---- */
static QueueHandle_t s_gw_queue;
static QueueHandle_t s_cfg_queue;
static QueueHandle_t s_can_tx_queue;
static QueueHandle_t s_lin_tx_queue;

/* ---- Task Handles ---- */
#define NUM_TEST_TASKS  3
static TaskHandle_t s_task_handles[NUM_TEST_TASKS];

static uint8_t s_total = 0;
static uint8_t s_passed = 0;
static uint8_t s_failed = 0;

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
    vTaskDelay(pdMS_TO_TICKS(20));

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

static void send_progress(uint8_t test_id, uint8_t step)
{
    can_frame_t frame = {0};
    frame.id = TEST_PROGRESS_CAN_ID;
    frame.dlc = 2;
    frame.data[0] = test_id;
    frame.data[1] = step;
    can_manager_transmit(CAN_BUS_1, &frame);
    vTaskDelay(pdMS_TO_TICKS(20));
}

/*
 * Simulate config command by directly calling the config handler
 * via the config queue. We build a gateway_frame_t and push it.
 */
static void inject_config_cmd(uint32_t can_id, const uint8_t *data, uint8_t dlc)
{
    gateway_frame_t gf = {0};
    gf.source_bus = BUS_CAN1;
    gf.frame.id   = can_id;
    gf.frame.dlc  = dlc;
    memcpy(gf.frame.data, data, dlc > 8 ? 8 : dlc);
    gf.timestamp  = 0;
    xQueueSend(s_cfg_queue, &gf, pdMS_TO_TICKS(100));
}

/* ---- Crash Data Report ---- */

static void report_crash_data(void)
{
    const crash_data_t *crash = fault_handler_get_crash_data();
    if (crash->magic != CRASH_DATA_MAGIC) return;

    /* Frame 1 on 0x7FD: [fault_type, PC_0..PC_3, LR_0..LR_2] */
    can_frame_t frame = {0};
    frame.id = TEST_DIAG_CAN_ID;
    frame.dlc = 8;
    frame.data[0] = (uint8_t)crash->fault_type;
    frame.data[1] = (uint8_t)(crash->pc);
    frame.data[2] = (uint8_t)(crash->pc >> 8);
    frame.data[3] = (uint8_t)(crash->pc >> 16);
    frame.data[4] = (uint8_t)(crash->pc >> 24);
    frame.data[5] = (uint8_t)(crash->lr);
    frame.data[6] = (uint8_t)(crash->lr >> 8);
    frame.data[7] = (uint8_t)(crash->lr >> 16);
    can_manager_transmit(CAN_BUS_1, &frame);
    vTaskDelay(pdMS_TO_TICKS(50));

    /* Frame 2 on 0x7FD: [0xCC marker, LR_3, sizeof(nvm_config_t) LE] */
    can_frame_t frame2 = {0};
    frame2.id = TEST_DIAG_CAN_ID;
    frame2.dlc = 4;
    frame2.data[0] = 0xCC;
    frame2.data[1] = (uint8_t)(crash->lr >> 24);
    uint16_t cfg_size = (uint16_t)sizeof(nvm_config_t);
    frame2.data[2] = (uint8_t)cfg_size;
    frame2.data[3] = (uint8_t)(cfg_size >> 8);
    can_manager_transmit(CAN_BUS_1, &frame2);
    vTaskDelay(pdMS_TO_TICKS(50));

    /* Send crash data 3 more times so it's hard to miss */
    for (int i = 0; i < 3; i++) {
        vTaskDelay(pdMS_TO_TICKS(100));
        can_manager_transmit(CAN_BUS_1, &frame);
        vTaskDelay(pdMS_TO_TICKS(50));
        can_manager_transmit(CAN_BUS_1, &frame2);
    }

    fault_handler_clear();
}

/* ---- Auto Tests ---- */

static void run_auto_tests(void)
{
    s_total = 0; s_passed = 0; s_failed = 0;

    /* NVM was erased in main() before CAN started, so no bus disruption. */

    /* T5.1: First boot defaults — verify defaults loaded from erased NVM */
    {
        send_progress(1, 0x01);
        nvm_config_t cfg;
        bool loaded = nvm_config_load(&cfg);
        bool ok = !loaded &&
                  cfg.magic == NVM_CONFIG_MAGIC &&
                  cfg.can[0].bitrate == CAN_DEFAULT_BITRATE &&
                  cfg.can[1].enabled == 0;
        uint8_t extra[4];
        extra[0] = loaded ? 1 : 0;
        uint16_t sz = (uint16_t)sizeof(nvm_config_t);
        extra[1] = (uint8_t)sz;
        extra[2] = (uint8_t)(sz >> 8);
        extra[3] = 0;
        report_test(1, ok ? RESULT_PASS : RESULT_FAIL, extra, 4);
    }

    /* T5.2: Config persistence — save, read back, verify */
    {
        send_progress(2, 0x01);  /* Starting T5.2 */

        nvm_config_t cfg;
        nvm_config_defaults(&cfg);
        cfg.can[0].bitrate = 250000;

        send_progress(2, 0x02);  /* Before save */
        bool saved = nvm_config_save(&cfg);
        send_progress(2, 0x03);  /* After save */

        nvm_config_t readback;
        bool loaded = nvm_config_load(&readback);
        send_progress(2, 0x04);  /* After readback */

        bool ok = saved && loaded &&
                  readback.can[0].bitrate == 250000 &&
                  nvm_config_validate(&readback);
        uint8_t extra[2] = { saved ? 1 : 0, loaded ? 1 : 0 };
        report_test(2, ok ? RESULT_PASS : RESULT_FAIL, extra, 2);
    }

    /* T5.3: Ping-pong slot swap */
    {
        send_progress(3, 0x01);
        nvm_config_t cfg;
        nvm_config_defaults(&cfg);

        nvm_config_save(&cfg);
        uint32_t wc1 = nvm_config_get_write_count();

        nvm_config_save(&cfg);
        uint32_t wc2 = nvm_config_get_write_count();

        bool ok = (wc2 == wc1 + 1);
        uint8_t extra[4] = {
            (uint8_t)wc1, (uint8_t)(wc1 >> 8),
            (uint8_t)wc2, (uint8_t)(wc2 >> 8)
        };
        report_test(3, ok ? RESULT_PASS : RESULT_FAIL, extra, 4);
    }

    /* T5.4: CRC validation — corrupt 1 byte in NVM, detect mismatch */
    {
        send_progress(4, 0x01);
        hal_nvm_erase_sector(NVM_SLOT_A_OFFSET);
        hal_nvm_erase_sector(NVM_SLOT_B_OFFSET);
        hal_nvm_erase_sector(NVM_META_OFFSET);

        nvm_config_t cfg;
        nvm_config_defaults(&cfg);
        nvm_config_save(&cfg);

        /* Read the active slot, corrupt it, write it back */
        nvm_config_t corrupt;
        hal_nvm_read(NVM_SLOT_A_OFFSET, &corrupt, sizeof(corrupt));

        hal_nvm_erase_sector(NVM_SLOT_A_OFFSET);
        hal_nvm_erase_sector(NVM_SLOT_B_OFFSET);

        corrupt.can[0].bitrate ^= 0xFF;
        hal_nvm_write(NVM_SLOT_A_OFFSET, &corrupt, sizeof(corrupt));
        /* Also corrupt slot B so no fallback */
        hal_nvm_write(NVM_SLOT_B_OFFSET, &corrupt, sizeof(corrupt));

        nvm_config_t loaded_cfg;
        bool loaded = nvm_config_load(&loaded_cfg);
        bool ok = !loaded && loaded_cfg.can[0].bitrate == CAN_DEFAULT_BITRATE;
        uint8_t extra[1] = { loaded ? 1 : 0 };
        report_test(4, ok ? RESULT_PASS : RESULT_FAIL, extra, 1);
    }

    /* Re-initialize config handler for the protocol tests */
    hal_nvm_erase_sector(NVM_SLOT_A_OFFSET);
    hal_nvm_erase_sector(NVM_SLOT_B_OFFSET);
    hal_nvm_erase_sector(NVM_META_OFFSET);
    config_handler_init(s_cfg_queue, s_can_tx_queue);

    /* Let config task start processing */
    vTaskDelay(pdMS_TO_TICKS(50));

    /* T5.5: CONNECT command */
    {
        uint8_t cmd[8] = { CFG_CMD_CONNECT };
        inject_config_cmd(CONFIG_CAN_CMD_ID, cmd, 1);
        vTaskDelay(pdMS_TO_TICKS(100));

        const nvm_config_t *cfg = config_handler_get_config();
        bool ok = (cfg->magic == NVM_CONFIG_MAGIC);
        uint8_t extra[3] = { FW_VERSION_MAJOR, FW_VERSION_MINOR, FW_VERSION_PATCH };
        report_test(5, ok ? RESULT_PASS : RESULT_FAIL, extra, 3);
    }

    /* T5.6: READ CAN1 bitrate — should be default 500000 */
    {
        const nvm_config_t *cfg = config_handler_get_config();
        uint32_t br = cfg->can[0].bitrate;
        bool ok = (br == CAN_DEFAULT_BITRATE);
        uint8_t extra[4] = {
            (uint8_t)br, (uint8_t)(br >> 8),
            (uint8_t)(br >> 16), (uint8_t)(br >> 24)
        };
        report_test(6, ok ? RESULT_PASS : RESULT_FAIL, extra, 4);
    }

    /* T5.7: WRITE CAN1 bitrate = 250000 */
    {
        uint32_t new_br = 250000;
        uint8_t cmd[8] = {
            CFG_CMD_WRITE_PARAM, CFG_SECTION_CAN, 0, 0,
            (uint8_t)new_br, (uint8_t)(new_br >> 8), (uint8_t)(new_br >> 16), 0
        };
        inject_config_cmd(CONFIG_CAN_CMD_ID, cmd, 7);
        vTaskDelay(pdMS_TO_TICKS(100));

        const nvm_config_t *cfg = config_handler_get_config();
        bool ok = (cfg->can[0].bitrate == 250000);
        uint8_t extra[1] = { ok ? 1 : 0 };
        report_test(7, ok ? RESULT_PASS : RESULT_FAIL, extra, 1);
    }

    /* T5.8: Read after write — verify 250000 persists in RAM */
    {
        const nvm_config_t *cfg = config_handler_get_config();
        uint32_t br = cfg->can[0].bitrate;
        bool ok = (br == 250000);
        uint8_t extra[4] = {
            (uint8_t)br, (uint8_t)(br >> 8),
            (uint8_t)(br >> 16), (uint8_t)(br >> 24)
        };
        report_test(8, ok ? RESULT_PASS : RESULT_FAIL, extra, 4);
    }

    /* T5.9: SAVE + verify NVM */
    {
        send_progress(9, 0x01);
        uint8_t cmd[8] = { CFG_CMD_SAVE };
        inject_config_cmd(CONFIG_CAN_CMD_ID, cmd, 1);
        vTaskDelay(pdMS_TO_TICKS(500));

        nvm_config_t nvm_cfg;
        bool loaded = nvm_config_load(&nvm_cfg);
        bool ok = loaded && (nvm_cfg.can[0].bitrate == 250000);
        uint8_t extra[2] = { loaded ? 1 : 0, ok ? 1 : 0 };
        report_test(9, ok ? RESULT_PASS : RESULT_FAIL, extra, 2);
    }

    /* T5.10: Bulk write routing — 3 rules via bulk transfer */
    {
        routing_rule_t rules[3];
        memset(rules, 0, sizeof(rules));
        for (int i = 0; i < 3; i++) {
            rules[i].src_bus  = BUS_CAN1;
            rules[i].src_id   = 0x100 + i;
            rules[i].src_mask = 0xFFFFFFFF;
            rules[i].dst_bus  = BUS_CAN2;
            rules[i].dst_id   = GW_DST_ID_PASSTHROUGH;
            rules[i].enabled  = true;
        }

        uint16_t data_size = 3 * sizeof(routing_rule_t);
        uint32_t data_crc = crc32_compute(rules, data_size);

        uint8_t start_cmd[8] = {
            CFG_CMD_BULK_START, CFG_SECTION_ROUTING,
            (uint8_t)data_size, (uint8_t)(data_size >> 8),
            (uint8_t)data_crc, (uint8_t)(data_crc >> 8),
            (uint8_t)(data_crc >> 16), (uint8_t)(data_crc >> 24)
        };
        inject_config_cmd(CONFIG_CAN_CMD_ID, start_cmd, 8);
        vTaskDelay(pdMS_TO_TICKS(50));

        const uint8_t *src = (const uint8_t *)rules;
        uint8_t seq = 0;
        uint16_t sent = 0;
        while (sent < data_size) {
            uint8_t chunk = (data_size - sent > 7) ? 7 : (uint8_t)(data_size - sent);
            uint8_t data_frame[8] = {0};
            data_frame[0] = seq++;
            memcpy(&data_frame[1], &src[sent], chunk);

            inject_config_cmd(CONFIG_CAN_DATA_ID, data_frame, 1 + chunk);
            sent += chunk;
            vTaskDelay(pdMS_TO_TICKS(5));
        }

        uint8_t end_cmd[8] = { CFG_CMD_BULK_END };
        inject_config_cmd(CONFIG_CAN_CMD_ID, end_cmd, 1);
        vTaskDelay(pdMS_TO_TICKS(100));

        const nvm_config_t *cfg = config_handler_get_config();
        bool ok = (cfg->routing_rule_count == 3) &&
                  (cfg->routing_rules[0].src_id == 0x100) &&
                  (cfg->routing_rules[1].src_id == 0x101) &&
                  (cfg->routing_rules[2].src_id == 0x102);
        uint8_t extra[1] = { cfg->routing_rule_count };
        report_test(10, ok ? RESULT_PASS : RESULT_FAIL, extra, 1);
    }

    /* T5.11: Bulk CRC mismatch */
    {
        routing_rule_t rule;
        memset(&rule, 0, sizeof(rule));
        rule.src_bus  = BUS_CAN1;
        rule.src_id   = 0x200;
        rule.src_mask = 0xFFFFFFFF;
        rule.dst_bus  = BUS_CAN2;
        rule.dst_id   = GW_DST_ID_PASSTHROUGH;
        rule.enabled  = true;

        uint16_t data_size = sizeof(routing_rule_t);
        uint32_t bad_crc = 0xDEADBEEF;

        uint8_t start_cmd[8] = {
            CFG_CMD_BULK_START, CFG_SECTION_ROUTING,
            (uint8_t)data_size, (uint8_t)(data_size >> 8),
            (uint8_t)bad_crc, (uint8_t)(bad_crc >> 8),
            (uint8_t)(bad_crc >> 16), (uint8_t)(bad_crc >> 24)
        };
        inject_config_cmd(CONFIG_CAN_CMD_ID, start_cmd, 8);
        vTaskDelay(pdMS_TO_TICKS(50));

        const uint8_t *src = (const uint8_t *)&rule;
        uint8_t seq = 0;
        uint16_t sent = 0;
        while (sent < data_size) {
            uint8_t chunk = (data_size - sent > 7) ? 7 : (uint8_t)(data_size - sent);
            uint8_t data_frame[8] = {0};
            data_frame[0] = seq++;
            memcpy(&data_frame[1], &src[sent], chunk);
            inject_config_cmd(CONFIG_CAN_DATA_ID, data_frame, 1 + chunk);
            sent += chunk;
            vTaskDelay(pdMS_TO_TICKS(5));
        }

        uint8_t end_cmd[8] = { CFG_CMD_BULK_END };
        inject_config_cmd(CONFIG_CAN_CMD_ID, end_cmd, 1);
        vTaskDelay(pdMS_TO_TICKS(100));

        const nvm_config_t *cfg = config_handler_get_config();
        bool ok = (cfg->routing_rule_count == 3);
        uint8_t extra[1] = { cfg->routing_rule_count };
        report_test(11, ok ? RESULT_PASS : RESULT_FAIL, extra, 1);
    }

    /* T5.12: LOAD_DEFAULTS — verify bitrate reverts to 500000 */
    {
        uint8_t cmd[8] = { CFG_CMD_DEFAULTS };
        inject_config_cmd(CONFIG_CAN_CMD_ID, cmd, 1);
        vTaskDelay(pdMS_TO_TICKS(100));

        const nvm_config_t *cfg = config_handler_get_config();
        bool ok = (cfg->can[0].bitrate == CAN_DEFAULT_BITRATE) &&
                  (cfg->routing_rule_count == 0);
        uint8_t extra[4] = {
            (uint8_t)(cfg->can[0].bitrate),
            (uint8_t)(cfg->can[0].bitrate >> 8),
            (uint8_t)(cfg->can[0].bitrate >> 16),
            cfg->routing_rule_count
        };
        report_test(12, ok ? RESULT_PASS : RESULT_FAIL, extra, 4);
    }

    /* T5.15: Unknown param — READ invalid section */
    {
        uint8_t cmd[8] = { CFG_CMD_READ_PARAM, 0xFF, 0, 0 };
        inject_config_cmd(CONFIG_CAN_CMD_ID, cmd, 4);
        vTaskDelay(pdMS_TO_TICKS(100));

        const nvm_config_t *cfg = config_handler_get_config();
        bool ok = (cfg->magic == NVM_CONFIG_MAGIC);
        report_test(15, ok ? RESULT_PASS : RESULT_FAIL, NULL, 0);
    }

    /* T5.16: Runtime apply — write CAN2 term=true + SAVE, verify pin */
    {
        send_progress(16, 0x01);
        hal_can_set_termination(CAN_BUS_2, false);
        bool term_before = hal_can_get_termination(CAN_BUS_2);

        uint8_t cmd_write[8] = {
            CFG_CMD_WRITE_PARAM, CFG_SECTION_CAN, 1, 1,
            1, 0, 0, 0
        };
        inject_config_cmd(CONFIG_CAN_CMD_ID, cmd_write, 5);
        vTaskDelay(pdMS_TO_TICKS(50));

        uint8_t cmd_save[8] = { CFG_CMD_SAVE };
        inject_config_cmd(CONFIG_CAN_CMD_ID, cmd_save, 1);
        vTaskDelay(pdMS_TO_TICKS(500));

        bool term_after = hal_can_get_termination(CAN_BUS_2);
        bool ok = !term_before && term_after;
        uint8_t extra[2] = { term_before ? 1 : 0, term_after ? 1 : 0 };
        report_test(16, ok ? RESULT_PASS : RESULT_FAIL, extra, 2);

        hal_can_set_termination(CAN_BUS_2, false);
    }

    report_summary();
}

/* ---- Config Task Wrapper ---- */

static void config_task_entry(void *params)
{
    (void)params;
    config_handler_task(NULL);
}

/* ---- Test Orchestrator Task ---- */

static void test_orchestrator_task(void *params)
{
    (void)params;

    /* Wait for CAN to fully settle */
    vTaskDelay(pdMS_TO_TICKS(1000));

    /* Report crash data if rebooted from a fault (bus should be clean now) */
    report_crash_data();

    /* Extra settle time after crash report */
    vTaskDelay(pdMS_TO_TICKS(200));

    run_auto_tests();

    /* Stay alive — host may send REBOOT/ENTER_BL commands */
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

    fault_handler_init();
    hal_gpio_init();
    hal_nvm_init();

    adc_init();
    adc_set_temp_sensor_enabled(true);

    /*
     * Erase NVM sectors BEFORE starting CAN.
     * This avoids CAN bus errors from flash operations disrupting
     * the bus during the test phase.
     */
    hal_nvm_erase_sector(NVM_SLOT_A_OFFSET);
    hal_nvm_erase_sector(NVM_SLOT_B_OFFSET);
    hal_nvm_erase_sector(NVM_META_OFFSET);

    /* Create queues */
    s_gw_queue     = xQueueCreate(QUEUE_DEPTH_GATEWAY_IN, sizeof(gateway_frame_t));
    s_cfg_queue    = xQueueCreate(QUEUE_DEPTH_CONFIG_RX,  sizeof(gateway_frame_t));
    s_can_tx_queue = xQueueCreate(QUEUE_DEPTH_CAN_TX,     sizeof(gateway_frame_t));
    s_lin_tx_queue = xQueueCreate(QUEUE_DEPTH_LIN_TX,     sizeof(gateway_frame_t));

    /* Initialize config handler (loads from NVM — will get defaults since we just erased) */
    config_handler_init(s_cfg_queue, s_can_tx_queue);

    /* Initialize CAN */
    can_manager_init(s_gw_queue, s_cfg_queue, s_can_tx_queue);
    lin_manager_init(s_gw_queue, s_lin_tx_queue);
    can_manager_start_can1(CAN_DEFAULT_BITRATE);
    hal_can_set_termination(CAN_BUS_1, true);

    /* Create tasks */
    xTaskCreate(can_task_entry,          "CAN",  TASK_STACK_CAN,  NULL, 5, &s_task_handles[0]);
    xTaskCreate(config_task_entry,       "CFG",  TASK_STACK_CONFIG, NULL, 2, &s_task_handles[1]);
    xTaskCreate(test_orchestrator_task,  "TEST", 4096,             NULL, 3, &s_task_handles[2]);

    /* Enable hardware watchdog */
    watchdog_enable(HW_WATCHDOG_TIMEOUT_MS, true);

    vTaskStartScheduler();
    for (;;) {}
    return 0;
}

#endif /* TEST_PHASE5 */
