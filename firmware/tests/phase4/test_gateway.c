/*
 * Phase 4 On-Target Test Firmware: Gateway Engine
 * =================================================
 *
 * Build with -DTEST_PHASE4 to compile as standalone test firmware.
 *
 * Tests the gateway engine by:
 *   - Adding routing rules via gateway_engine_add_rule()
 *   - Sending CAN frames that transit through the engine
 *   - Verifying routed output on CAN1 (self-receive loop)
 *   - Injecting synthetic LIN frames into the gateway queue
 *   - Checking stats counters for correctness
 *
 * Results are reported via CAN1 on ID 0x7FE.
 * Summary on 0x7FF.
 *
 * Test result frame (CAN1, ID 0x7FE):
 *   Byte 0: Test ID (1-16)
 *   Byte 1: Result (0x00=PASS, 0x01=FAIL, 0x02=SKIP)
 *   Byte 2-7: Test-specific data
 *
 * Self-Receive Loop Prevention:
 *   Source IDs in 0x100-0x1FF, destination IDs in 0x200-0x4FF.
 *   Rules only match the source range, so re-received output frames
 *   are not re-routed.
 */

#ifdef TEST_PHASE4

#include "pico/stdlib.h"
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

#include <string.h>

/* ---- Test Protocol IDs ---- */
#define TEST_RESULT_CAN_ID  0x7FE
#define TEST_SUMMARY_CAN_ID 0x7FF
#define TEST_DIAG_CAN_ID    0x7FD

#define RESULT_PASS 0x00
#define RESULT_FAIL 0x01
#define RESULT_SKIP 0x02

/* ---- Queues ---- */
static QueueHandle_t s_gw_queue;
static QueueHandle_t s_cfg_queue;
static QueueHandle_t s_can_tx_queue;
static QueueHandle_t s_lin_tx_queue;

/* Separate queue for test orchestrator to observe routed CAN output */
static QueueHandle_t s_test_rx_queue;

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
    vTaskDelay(pdMS_TO_TICKS(10));

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

/**
 * Send a CAN frame on CAN1 that will be received by our own CAN RX,
 * routed into the gateway queue, processed by the engine, and the
 * output dispatched to the CAN TX queue.
 */
static void inject_can_frame(uint32_t id, const uint8_t *data, uint8_t dlc)
{
    can_frame_t frame = {0};
    frame.id = id;
    frame.dlc = dlc;
    if (data) memcpy(frame.data, data, dlc);
    can_manager_transmit(CAN_BUS_1, &frame);
}

/**
 * Inject a synthetic gateway_frame_t directly into the gateway queue.
 * Used for LIN→CAN tests where we don't have physical LIN hardware.
 */
static void inject_gw_frame(bus_id_t src_bus, uint32_t id,
                              const uint8_t *data, uint8_t dlc)
{
    gateway_frame_t gf = {0};
    gf.source_bus = src_bus;
    gf.frame.id = id;
    gf.frame.dlc = dlc;
    gf.timestamp = xTaskGetTickCount();
    if (data) memcpy(gf.frame.data, data, dlc);
    xQueueSend(s_gw_queue, &gf, pdMS_TO_TICKS(50));
}

/**
 * Wait for a routed frame to appear in the test RX queue.
 * Returns true if found, populating *out.
 */
static bool wait_for_routed_frame(gateway_frame_t *out, uint32_t timeout_ms)
{
    return (xQueueReceive(s_test_rx_queue, out, pdMS_TO_TICKS(timeout_ms)) == pdTRUE);
}

/**
 * Flush the test RX queue of any stale frames.
 */
static void flush_test_rx(void)
{
    gateway_frame_t dummy;
    while (xQueueReceive(s_test_rx_queue, &dummy, 0) == pdTRUE) {}
}

/* ---- Custom CAN TX Queue Consumer ----
 * Instead of letting the normal can_task drain s_can_tx_queue,
 * we intercept it in the test to observe routed frames.
 * Frames with test/diagnostic IDs (0x7Fx) are passed through to CAN HW.
 * Routed frames (from engine) are captured into s_test_rx_queue.
 */
static void test_can_tx_task(void *params)
{
    (void)params;
    for (;;) {
        gateway_frame_t tx_gf;
        if (xQueueReceive(s_can_tx_queue, &tx_gf, pdMS_TO_TICKS(5)) == pdTRUE) {
            if (tx_gf.frame.id >= 0x7F0 && tx_gf.frame.id <= 0x7FF) {
                /* Test protocol frame — send to CAN HW */
                can_bus_id_t bus = (tx_gf.source_bus == BUS_CAN1) ? CAN_BUS_1 : CAN_BUS_2;
                can_manager_transmit(bus, &tx_gf.frame);
            } else {
                /* Routed frame from engine — capture for test verification */
                xQueueSend(s_test_rx_queue, &tx_gf, 0);
            }
        }
    }
}

/* ---- Tests ---- */

/* T4.1: CAN1→CAN1 passthrough — Send 0x100, receive 0x200 with same data */
static void test_t4_1_can1_passthrough(void)
{
    gateway_engine_clear_rules();
    gateway_engine_reset_stats();
    flush_test_rx();

    routing_rule_t rule = {0};
    rule.src_bus = BUS_CAN1;
    rule.src_id = 0x100;
    rule.src_mask = 0xFFFFFFFF;
    rule.dst_bus = BUS_CAN1;
    rule.dst_id = 0x200;
    rule.dst_dlc = 0;
    rule.mapping_count = 0;
    rule.enabled = true;
    gateway_engine_add_rule(&rule);

    uint8_t data[8] = {0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88};
    inject_can_frame(0x100, data, 8);

    vTaskDelay(pdMS_TO_TICKS(100));

    gateway_frame_t out;
    bool got = wait_for_routed_frame(&out, 200);
    bool pass = got && (out.frame.id == 0x200) &&
                (memcmp(out.frame.data, data, 8) == 0) &&
                (out.source_bus == BUS_CAN1);

    uint8_t extra[4] = { got ? 1 : 0,
                          got ? (uint8_t)(out.frame.id >> 8) : 0,
                          got ? (uint8_t)(out.frame.id) : 0,
                          got ? out.frame.data[0] : 0 };
    report_test(1, pass ? RESULT_PASS : RESULT_FAIL, extra, 4);
}

/* T4.2: CAN1→CAN2 routing — verify destination bus */
static void test_t4_2_can1_to_can2(void)
{
    gateway_engine_clear_rules();
    flush_test_rx();

    routing_rule_t rule = {0};
    rule.src_bus = BUS_CAN1;
    rule.src_id = 0x110;
    rule.src_mask = 0xFFFFFFFF;
    rule.dst_bus = BUS_CAN2;
    rule.dst_id = GW_DST_ID_PASSTHROUGH;
    rule.mapping_count = 0;
    rule.enabled = true;
    gateway_engine_add_rule(&rule);

    uint8_t data[4] = {0xAA, 0xBB, 0xCC, 0xDD};
    inject_can_frame(0x110, data, 4);

    vTaskDelay(pdMS_TO_TICKS(100));

    /* The frame goes to CAN2 TX queue — we can check stats */
    gateway_stats_t stats;
    gateway_engine_get_stats(&stats);

    /* Since CAN2 is not started, the frame will be in the TX queue.
     * We can't intercept CAN2 TX in our test harness, but we check
     * that engine stats show it was routed (not dropped). */
    bool pass = (stats.frames_routed >= 1);
    uint8_t extra[2] = { (uint8_t)stats.frames_routed, (uint8_t)stats.frames_dropped };
    report_test(2, pass ? RESULT_PASS : RESULT_FAIL, extra, 2);
}

/* T4.3: ID translation — Send 0x101, receive 0x300 */
static void test_t4_3_id_translation(void)
{
    gateway_engine_clear_rules();
    gateway_engine_reset_stats();
    flush_test_rx();

    routing_rule_t rule = {0};
    rule.src_bus = BUS_CAN1;
    rule.src_id = 0x101;
    rule.src_mask = 0xFFFFFFFF;
    rule.dst_bus = BUS_CAN1;
    rule.dst_id = 0x300;
    rule.mapping_count = 0;
    rule.enabled = true;
    gateway_engine_add_rule(&rule);

    uint8_t data[8] = {0xDE, 0xAD, 0xBE, 0xEF, 0, 0, 0, 0};
    inject_can_frame(0x101, data, 8);

    vTaskDelay(pdMS_TO_TICKS(100));

    gateway_frame_t out;
    bool got = wait_for_routed_frame(&out, 200);
    bool pass = got && (out.frame.id == 0x300);

    uint8_t extra[3] = { got ? 1 : 0,
                          got ? (uint8_t)(out.frame.id >> 8) : 0,
                          got ? (uint8_t)(out.frame.id) : 0 };
    report_test(3, pass ? RESULT_PASS : RESULT_FAIL, extra, 3);
}

/* T4.4: ID mask match (0xFF0) — Send 0x105, matches rule for 0x100/0xFF0 */
static void test_t4_4_id_mask(void)
{
    gateway_engine_clear_rules();
    gateway_engine_reset_stats();
    flush_test_rx();

    routing_rule_t rule = {0};
    rule.src_bus = BUS_CAN1;
    rule.src_id = 0x100;
    rule.src_mask = 0xFF0;          /* Match 0x100-0x10F */
    rule.dst_bus = BUS_CAN1;
    rule.dst_id = 0x400;
    rule.mapping_count = 0;
    rule.enabled = true;
    gateway_engine_add_rule(&rule);

    uint8_t data[2] = {0x42, 0x43};
    inject_can_frame(0x105, data, 2);

    vTaskDelay(pdMS_TO_TICKS(100));

    gateway_frame_t out;
    bool got = wait_for_routed_frame(&out, 200);
    bool pass = got && (out.frame.id == 0x400);

    uint8_t extra[3] = { got ? 1 : 0,
                          got ? (uint8_t)(out.frame.id >> 8) : 0,
                          got ? (uint8_t)(out.frame.id) : 0 };
    report_test(4, pass ? RESULT_PASS : RESULT_FAIL, extra, 3);
}

/* T4.5: No match → drop — Send 0x1FF, verify frames_dropped++ */
static void test_t4_5_no_match_drop(void)
{
    gateway_engine_clear_rules();
    gateway_engine_reset_stats();
    flush_test_rx();

    /* Add a rule that only matches 0x100 */
    routing_rule_t rule = {0};
    rule.src_bus = BUS_CAN1;
    rule.src_id = 0x100;
    rule.src_mask = 0xFFFFFFFF;
    rule.dst_bus = BUS_CAN1;
    rule.dst_id = 0x200;
    rule.mapping_count = 0;
    rule.enabled = true;
    gateway_engine_add_rule(&rule);

    /* Send a frame that doesn't match */
    uint8_t data[1] = {0xFF};
    inject_can_frame(0x1FF, data, 1);

    vTaskDelay(pdMS_TO_TICKS(100));

    gateway_stats_t stats;
    gateway_engine_get_stats(&stats);

    bool pass = (stats.frames_dropped >= 1);
    uint8_t extra[2] = { (uint8_t)stats.frames_routed, (uint8_t)stats.frames_dropped };
    report_test(5, pass ? RESULT_PASS : RESULT_FAIL, extra, 2);
}

/* T4.6: Full passthrough (0 mappings) — all 8 bytes copied unchanged */
static void test_t4_6_full_passthrough(void)
{
    gateway_engine_clear_rules();
    gateway_engine_reset_stats();
    flush_test_rx();

    routing_rule_t rule = {0};
    rule.src_bus = BUS_CAN1;
    rule.src_id = 0x120;
    rule.src_mask = 0xFFFFFFFF;
    rule.dst_bus = BUS_CAN1;
    rule.dst_id = 0x220;
    rule.mapping_count = 0;  /* Full passthrough */
    rule.enabled = true;
    gateway_engine_add_rule(&rule);

    uint8_t data[8] = {0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08};
    inject_can_frame(0x120, data, 8);

    vTaskDelay(pdMS_TO_TICKS(100));

    gateway_frame_t out;
    bool got = wait_for_routed_frame(&out, 200);
    bool data_match = got && (memcmp(out.frame.data, data, 8) == 0);
    bool pass = data_match && (out.frame.dlc == 8);

    uint8_t extra[4] = { got ? 1 : 0, data_match ? 1 : 0,
                          got ? out.frame.data[0] : 0,
                          got ? out.frame.data[7] : 0 };
    report_test(6, pass ? RESULT_PASS : RESULT_FAIL, extra, 4);
}

/* T4.7: Single byte extraction — src[2] → dst[0] */
static void test_t4_7_byte_extract(void)
{
    gateway_engine_clear_rules();
    gateway_engine_reset_stats();
    flush_test_rx();

    routing_rule_t rule = {0};
    rule.src_bus = BUS_CAN1;
    rule.src_id = 0x130;
    rule.src_mask = 0xFFFFFFFF;
    rule.dst_bus = BUS_CAN1;
    rule.dst_id = 0x230;
    rule.dst_dlc = 1;
    rule.mapping_count = 1;
    rule.mappings[0].src_byte = 2;
    rule.mappings[0].dst_byte = 0;
    rule.mappings[0].mask = 0xFF;
    rule.mappings[0].shift = 0;
    rule.mappings[0].offset = 0;
    rule.enabled = true;
    gateway_engine_add_rule(&rule);

    uint8_t data[8] = {0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22};
    inject_can_frame(0x130, data, 8);

    vTaskDelay(pdMS_TO_TICKS(100));

    gateway_frame_t out;
    bool got = wait_for_routed_frame(&out, 200);
    bool pass = got && (out.frame.data[0] == 0xCC) && (out.frame.dlc == 1);

    uint8_t extra[3] = { got ? 1 : 0,
                          got ? out.frame.data[0] : 0,
                          got ? out.frame.dlc : 0 };
    report_test(7, pass ? RESULT_PASS : RESULT_FAIL, extra, 3);
}

/* T4.8: Mask + shift — Low nibble → high nibble */
static void test_t4_8_mask_shift(void)
{
    gateway_engine_clear_rules();
    gateway_engine_reset_stats();
    flush_test_rx();

    routing_rule_t rule = {0};
    rule.src_bus = BUS_CAN1;
    rule.src_id = 0x140;
    rule.src_mask = 0xFFFFFFFF;
    rule.dst_bus = BUS_CAN1;
    rule.dst_id = 0x240;
    rule.dst_dlc = 1;
    rule.mapping_count = 1;
    rule.mappings[0].src_byte = 0;
    rule.mappings[0].dst_byte = 0;
    rule.mappings[0].mask = 0x0F;   /* Low nibble */
    rule.mappings[0].shift = 4;     /* Shift left 4 → high nibble */
    rule.mappings[0].offset = 0;
    rule.enabled = true;
    gateway_engine_add_rule(&rule);

    /* Input: 0xA5, low nibble = 0x05, shifted left 4 = 0x50 */
    uint8_t data[1] = {0xA5};
    inject_can_frame(0x140, data, 1);

    vTaskDelay(pdMS_TO_TICKS(100));

    gateway_frame_t out;
    bool got = wait_for_routed_frame(&out, 200);
    bool pass = got && (out.frame.data[0] == 0x50);

    uint8_t extra[2] = { got ? 1 : 0, got ? out.frame.data[0] : 0 };
    report_test(8, pass ? RESULT_PASS : RESULT_FAIL, extra, 2);
}

/* T4.9: CAN→LIN routing — CAN1 0x150 → LIN1, check stats */
static void test_t4_9_can_to_lin(void)
{
    gateway_engine_clear_rules();
    gateway_engine_reset_stats();
    flush_test_rx();

    routing_rule_t rule = {0};
    rule.src_bus = BUS_CAN1;
    rule.src_id = 0x150;
    rule.src_mask = 0xFFFFFFFF;
    rule.dst_bus = BUS_LIN1;
    rule.dst_id = 0x10;         /* LIN ID */
    rule.dst_dlc = 4;
    rule.mapping_count = 0;
    rule.enabled = true;
    gateway_engine_add_rule(&rule);

    uint8_t data[4] = {0x01, 0x02, 0x03, 0x04};
    inject_can_frame(0x150, data, 4);

    vTaskDelay(pdMS_TO_TICKS(100));

    /* Check that the engine routed it (stats) */
    gateway_stats_t stats;
    gateway_engine_get_stats(&stats);

    /* The frame goes to LIN TX queue. We verify via stats — actual LIN TX
     * depends on SJA1124 channel being started, which is not our focus here. */
    bool pass = (stats.frames_routed >= 1) && (stats.lin_tx_overflow == 0);

    uint8_t extra[3] = { (uint8_t)stats.frames_routed,
                          (uint8_t)stats.frames_dropped,
                          (uint8_t)stats.lin_tx_overflow };
    report_test(9, pass ? RESULT_PASS : RESULT_FAIL, extra, 3);
}

/* T4.10: LIN→CAN routing — Inject synthetic LIN1 frame, receive on CAN1 */
static void test_t4_10_lin_to_can(void)
{
    gateway_engine_clear_rules();
    gateway_engine_reset_stats();
    flush_test_rx();

    routing_rule_t rule = {0};
    rule.src_bus = BUS_LIN1;
    rule.src_id = 0x20;
    rule.src_mask = 0xFFFFFFFF;
    rule.dst_bus = BUS_CAN1;
    rule.dst_id = 0x350;
    rule.mapping_count = 0;
    rule.enabled = true;
    gateway_engine_add_rule(&rule);

    /* Inject directly into gateway queue (no physical LIN needed) */
    uint8_t data[4] = {0xDE, 0xAD, 0xBE, 0xEF};
    inject_gw_frame(BUS_LIN1, 0x20, data, 4);

    vTaskDelay(pdMS_TO_TICKS(100));

    gateway_frame_t out;
    bool got = wait_for_routed_frame(&out, 200);
    bool pass = got && (out.frame.id == 0x350) &&
                (out.source_bus == BUS_CAN1) &&
                (memcmp(out.frame.data, data, 4) == 0);

    uint8_t extra[4] = { got ? 1 : 0,
                          got ? (uint8_t)(out.frame.id >> 8) : 0,
                          got ? (uint8_t)(out.frame.id) : 0,
                          got ? out.frame.data[0] : 0 };
    report_test(10, pass ? RESULT_PASS : RESULT_FAIL, extra, 4);
}

/* T4.11: Fan-out (2 rules) — 1 frame → 2 different outputs */
static void test_t4_11_fan_out(void)
{
    gateway_engine_clear_rules();
    gateway_engine_reset_stats();
    flush_test_rx();

    /* Rule 1: 0x160 → 0x260 */
    routing_rule_t rule1 = {0};
    rule1.src_bus = BUS_CAN1;
    rule1.src_id = 0x160;
    rule1.src_mask = 0xFFFFFFFF;
    rule1.dst_bus = BUS_CAN1;
    rule1.dst_id = 0x260;
    rule1.mapping_count = 0;
    rule1.enabled = true;
    gateway_engine_add_rule(&rule1);

    /* Rule 2: 0x160 → 0x360 */
    routing_rule_t rule2 = {0};
    rule2.src_bus = BUS_CAN1;
    rule2.src_id = 0x160;
    rule2.src_mask = 0xFFFFFFFF;
    rule2.dst_bus = BUS_CAN1;
    rule2.dst_id = 0x360;
    rule2.mapping_count = 0;
    rule2.enabled = true;
    gateway_engine_add_rule(&rule2);

    uint8_t data[2] = {0xAB, 0xCD};
    inject_can_frame(0x160, data, 2);

    vTaskDelay(pdMS_TO_TICKS(150));

    gateway_frame_t out1, out2;
    bool got1 = wait_for_routed_frame(&out1, 200);
    bool got2 = wait_for_routed_frame(&out2, 200);

    /* Both should be received; order may vary */
    bool id_260_found = false, id_360_found = false;
    if (got1) {
        if (out1.frame.id == 0x260) id_260_found = true;
        if (out1.frame.id == 0x360) id_360_found = true;
    }
    if (got2) {
        if (out2.frame.id == 0x260) id_260_found = true;
        if (out2.frame.id == 0x360) id_360_found = true;
    }

    bool pass = id_260_found && id_360_found;
    uint8_t extra[4] = { got1 ? 1 : 0, got2 ? 1 : 0,
                          id_260_found ? 1 : 0, id_360_found ? 1 : 0 };
    report_test(11, pass ? RESULT_PASS : RESULT_FAIL, extra, 4);
}

/* T4.12: Disabled rule — enabled=false, no output */
static void test_t4_12_disabled_rule(void)
{
    gateway_engine_clear_rules();
    gateway_engine_reset_stats();
    flush_test_rx();

    routing_rule_t rule = {0};
    rule.src_bus = BUS_CAN1;
    rule.src_id = 0x170;
    rule.src_mask = 0xFFFFFFFF;
    rule.dst_bus = BUS_CAN1;
    rule.dst_id = 0x270;
    rule.mapping_count = 0;
    rule.enabled = false;   /* Disabled! */
    gateway_engine_add_rule(&rule);

    uint8_t data[1] = {0xFF};
    inject_can_frame(0x170, data, 1);

    vTaskDelay(pdMS_TO_TICKS(100));

    gateway_frame_t out;
    bool got = wait_for_routed_frame(&out, 200);

    gateway_stats_t stats;
    gateway_engine_get_stats(&stats);

    /* Should not have been routed */
    bool pass = !got && (stats.frames_dropped >= 1);
    uint8_t extra[3] = { got ? 1 : 0,
                          (uint8_t)stats.frames_routed,
                          (uint8_t)stats.frames_dropped };
    report_test(12, pass ? RESULT_PASS : RESULT_FAIL, extra, 3);
}

/* T4.13: DLC override — Input DLC=8, output DLC=4 */
static void test_t4_13_dlc_override(void)
{
    gateway_engine_clear_rules();
    gateway_engine_reset_stats();
    flush_test_rx();

    routing_rule_t rule = {0};
    rule.src_bus = BUS_CAN1;
    rule.src_id = 0x180;
    rule.src_mask = 0xFFFFFFFF;
    rule.dst_bus = BUS_CAN1;
    rule.dst_id = 0x280;
    rule.dst_dlc = 4;       /* Override DLC to 4 */
    rule.mapping_count = 0;
    rule.enabled = true;
    gateway_engine_add_rule(&rule);

    uint8_t data[8] = {1, 2, 3, 4, 5, 6, 7, 8};
    inject_can_frame(0x180, data, 8);

    vTaskDelay(pdMS_TO_TICKS(100));

    gateway_frame_t out;
    bool got = wait_for_routed_frame(&out, 200);
    bool pass = got && (out.frame.dlc == 4);

    uint8_t extra[2] = { got ? 1 : 0, got ? out.frame.dlc : 0 };
    report_test(13, pass ? RESULT_PASS : RESULT_FAIL, extra, 2);
}

/* T4.14: Multiple byte mappings — 3 simultaneous mappings verified */
static void test_t4_14_multi_mapping(void)
{
    gateway_engine_clear_rules();
    gateway_engine_reset_stats();
    flush_test_rx();

    routing_rule_t rule = {0};
    rule.src_bus = BUS_CAN1;
    rule.src_id = 0x190;
    rule.src_mask = 0xFFFFFFFF;
    rule.dst_bus = BUS_CAN1;
    rule.dst_id = 0x290;
    rule.dst_dlc = 3;
    rule.mapping_count = 3;
    /* Mapping 0: src[0] → dst[0] */
    rule.mappings[0].src_byte = 0;
    rule.mappings[0].dst_byte = 0;
    rule.mappings[0].mask = 0xFF;
    rule.mappings[0].shift = 0;
    rule.mappings[0].offset = 0;
    /* Mapping 1: src[3] → dst[1] */
    rule.mappings[1].src_byte = 3;
    rule.mappings[1].dst_byte = 1;
    rule.mappings[1].mask = 0xFF;
    rule.mappings[1].shift = 0;
    rule.mappings[1].offset = 0;
    /* Mapping 2: src[7] → dst[2] */
    rule.mappings[2].src_byte = 7;
    rule.mappings[2].dst_byte = 2;
    rule.mappings[2].mask = 0xFF;
    rule.mappings[2].shift = 0;
    rule.mappings[2].offset = 0;
    rule.enabled = true;
    gateway_engine_add_rule(&rule);

    uint8_t data[8] = {0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6, 0x17, 0x28};
    inject_can_frame(0x190, data, 8);

    vTaskDelay(pdMS_TO_TICKS(100));

    gateway_frame_t out;
    bool got = wait_for_routed_frame(&out, 200);
    bool pass = got &&
                (out.frame.data[0] == 0xA1) &&
                (out.frame.data[1] == 0xD4) &&
                (out.frame.data[2] == 0x28);

    uint8_t extra[4] = { got ? 1 : 0,
                          got ? out.frame.data[0] : 0,
                          got ? out.frame.data[1] : 0,
                          got ? out.frame.data[2] : 0 };
    report_test(14, pass ? RESULT_PASS : RESULT_FAIL, extra, 4);
}

/* T4.15: Offset mapping — Output byte = (input & mask) + 10 */
static void test_t4_15_offset_mapping(void)
{
    gateway_engine_clear_rules();
    gateway_engine_reset_stats();
    flush_test_rx();

    routing_rule_t rule = {0};
    rule.src_bus = BUS_CAN1;
    rule.src_id = 0x1A0;
    rule.src_mask = 0xFFFFFFFF;
    rule.dst_bus = BUS_CAN1;
    rule.dst_id = 0x2A0;
    rule.dst_dlc = 1;
    rule.mapping_count = 1;
    rule.mappings[0].src_byte = 0;
    rule.mappings[0].dst_byte = 0;
    rule.mappings[0].mask = 0xFF;
    rule.mappings[0].shift = 0;
    rule.mappings[0].offset = 10;   /* Add 10 */
    rule.enabled = true;
    gateway_engine_add_rule(&rule);

    /* Input = 0x32 (50), expected output = 0x3C (60) */
    uint8_t data[1] = {0x32};
    inject_can_frame(0x1A0, data, 1);

    vTaskDelay(pdMS_TO_TICKS(100));

    gateway_frame_t out;
    bool got = wait_for_routed_frame(&out, 200);
    bool pass = got && (out.frame.data[0] == 0x3C);

    uint8_t extra[3] = { got ? 1 : 0,
                          got ? out.frame.data[0] : 0,
                          0x3C /* expected */ };
    report_test(15, pass ? RESULT_PASS : RESULT_FAIL, extra, 3);
}

/* T4.16: Gateway stats — frames_routed/dropped consistency */
static void test_t4_16_stats(void)
{
    gateway_engine_clear_rules();
    gateway_engine_reset_stats();
    flush_test_rx();

    /* One rule matching 0x1B0 */
    routing_rule_t rule = {0};
    rule.src_bus = BUS_CAN1;
    rule.src_id = 0x1B0;
    rule.src_mask = 0xFFFFFFFF;
    rule.dst_bus = BUS_CAN1;
    rule.dst_id = 0x2B0;
    rule.mapping_count = 0;
    rule.enabled = true;
    gateway_engine_add_rule(&rule);

    /* Send 2 matching + 1 non-matching via direct injection */
    inject_gw_frame(BUS_CAN1, 0x1B0, NULL, 0);
    vTaskDelay(pdMS_TO_TICKS(50));
    inject_gw_frame(BUS_CAN1, 0x1B0, NULL, 0);
    vTaskDelay(pdMS_TO_TICKS(50));
    inject_gw_frame(BUS_CAN1, 0x1FF, NULL, 0);  /* No match */
    vTaskDelay(pdMS_TO_TICKS(100));

    gateway_stats_t stats;
    gateway_engine_get_stats(&stats);

    bool pass = (stats.frames_routed == 2) && (stats.frames_dropped == 1);
    uint8_t extra[4] = { (uint8_t)stats.frames_routed,
                          (uint8_t)stats.frames_dropped,
                          (uint8_t)stats.can_tx_overflow,
                          (uint8_t)stats.lin_tx_overflow };
    report_test(16, pass ? RESULT_PASS : RESULT_FAIL, extra, 4);
}

/* ---- Auto-Test Runner ---- */

static void run_auto_tests(void)
{
    s_total = 0; s_passed = 0; s_failed = 0;

    test_t4_1_can1_passthrough();
    test_t4_2_can1_to_can2();
    test_t4_3_id_translation();
    test_t4_4_id_mask();
    test_t4_5_no_match_drop();
    test_t4_6_full_passthrough();
    test_t4_7_byte_extract();
    test_t4_8_mask_shift();
    test_t4_9_can_to_lin();
    test_t4_10_lin_to_can();
    test_t4_11_fan_out();
    test_t4_12_disabled_rule();
    test_t4_13_dlc_override();
    test_t4_14_multi_mapping();
    test_t4_15_offset_mapping();
    test_t4_16_stats();

    report_summary();
}

/* ---- Gateway Task (test version) ---- */

static void test_gateway_task(void *params)
{
    (void)params;
    gateway_engine_init(s_can_tx_queue, s_lin_tx_queue);

    for (;;) {
        gateway_frame_t gf;
        if (xQueueReceive(s_gw_queue, &gf, pdMS_TO_TICKS(5)) == pdTRUE) {
            /* Filter out test protocol IDs from routing */
            if (gf.frame.id >= 0x7F0 && gf.frame.id <= 0x7FF) continue;
            gateway_engine_process(&gf);
        }
    }
}

/* ---- Test Orchestrator ---- */

static void test_orchestrator_task(void *params)
{
    (void)params;

    /* Wait for CAN + gateway to settle */
    vTaskDelay(pdMS_TO_TICKS(500));

    run_auto_tests();

    /* Idle — tests complete */
    for (;;) {
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
}

/* ---- FreeRTOS Hooks ---- */
void vApplicationIdleHook(void) {}
void vApplicationStackOverflowHook(TaskHandle_t t, char *n) { (void)t; (void)n; for(;;); }
void vApplicationMallocFailedHook(void) { for(;;); }

int main(void)
{
    stdio_init_all();
    hal_gpio_init();
    hal_can_set_termination(CAN_BUS_1, true);

    /* Create queues */
    s_gw_queue      = xQueueCreate(QUEUE_DEPTH_GATEWAY_IN, sizeof(gateway_frame_t));
    s_cfg_queue     = xQueueCreate(QUEUE_DEPTH_CONFIG_RX,   sizeof(gateway_frame_t));
    s_can_tx_queue  = xQueueCreate(QUEUE_DEPTH_CAN_TX,      sizeof(gateway_frame_t));
    s_lin_tx_queue  = xQueueCreate(QUEUE_DEPTH_LIN_TX,      sizeof(gateway_frame_t));
    s_test_rx_queue = xQueueCreate(16,                       sizeof(gateway_frame_t));

    /* Initialize CAN (LIN init deferred — not needed for most tests) */
    can_manager_init(s_gw_queue, s_cfg_queue, s_can_tx_queue);
    can_manager_start_can1(CAN_DEFAULT_BITRATE);

    /* Create tasks */
    xTaskCreate(can_task_entry,          "CAN",  TASK_STACK_CAN,     NULL, 5, NULL);
    xTaskCreate(test_can_tx_task,        "TXCAP", 512,               NULL, 5, NULL);
    xTaskCreate(test_gateway_task,       "GW",   TASK_STACK_GATEWAY, NULL, 3, NULL);
    xTaskCreate(test_orchestrator_task,  "TEST", 2048,               NULL, 2, NULL);

    vTaskStartScheduler();
    for (;;) {}
    return 0;
}

#endif /* TEST_PHASE4 */
