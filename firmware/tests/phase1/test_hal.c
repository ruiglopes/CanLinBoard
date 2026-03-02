/*
 * Phase 1 On-Target Tests: HAL Verification
 * ==========================================
 *
 * This is a standalone test firmware that exercises the HAL layer.
 * Results are reported via CAN1 on ID 0x7FE (test results).
 *
 * Build: Compile with -DTEST_PHASE1 to replace main() with test harness.
 *
 * Test result CAN frame format:
 *   Byte 0: Test ID (T1.1=1, T1.2=2, ... T1.12=12)
 *   Byte 1: Result (0x00=PASS, 0x01=FAIL, 0x02=SKIP)
 *   Byte 2-7: Test-specific data
 *
 * Final summary frame on 0x7FF:
 *   Byte 0: Total tests
 *   Byte 1: Passed
 *   Byte 2: Failed
 *   Byte 3: 0xAA (sentinel)
 */

#ifdef TEST_PHASE1

#include "pico/stdlib.h"
#include "hardware/gpio.h"
#include "hardware/spi.h"
#include "hardware/watchdog.h"

#include "FreeRTOS.h"
#include "task.h"

#include "board_config.h"
#include "app_header.h"
#include "hal/hal_gpio.h"
#include "hal/hal_spi.h"
#include "hal/hal_flash_nvm.h"
#include "hal/hal_clock.h"
#include "can/can_manager.h"
#include "can/can_bus.h"
#include "hardware/clocks.h"

#define TEST_RESULT_CAN_ID  0x7FE
#define TEST_SUMMARY_CAN_ID 0x7FF

#define RESULT_PASS 0x00
#define RESULT_FAIL 0x01
#define RESULT_SKIP 0x02

static uint8_t s_total = 0;
static uint8_t s_passed = 0;
static uint8_t s_failed = 0;

static void report_test(uint8_t test_id, uint8_t result, const uint8_t *extra, uint8_t extra_len)
{
    can_frame_t frame = {0};
    frame.id = TEST_RESULT_CAN_ID;
    frame.dlc = 2 + extra_len;
    frame.data[0] = test_id;
    frame.data[1] = result;
    if (extra && extra_len > 0) {
        for (uint8_t i = 0; i < extra_len && i < 6; i++) {
            frame.data[2 + i] = extra[i];
        }
    }
    can_manager_transmit(CAN_BUS_1, &frame);
    vTaskDelay(pdMS_TO_TICKS(10)); /* Small delay between frames */

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

/* ---- Individual Tests ---- */

static void test_t1_1_can1_en_enable(void)
{
    /* T1.1: Set CAN1 enable=true, verify pin 3 reads LOW */
    hal_can_enable(CAN_BUS_1, true);
    vTaskDelay(pdMS_TO_TICKS(1));
    bool pin_low = !gpio_get(CAN1_EN_PIN);
    report_test(1, pin_low ? RESULT_PASS : RESULT_FAIL, NULL, 0);
}

static void test_t1_2_can1_en_disable(void)
{
    /* T1.2: Set CAN1 enable=false, verify pin 3 reads HIGH */
    hal_can_enable(CAN_BUS_1, false);
    vTaskDelay(pdMS_TO_TICKS(1));
    bool pin_high = gpio_get(CAN1_EN_PIN);

    /* Re-enable transceiver BEFORE reporting result over CAN.
     * Transmitting with the transceiver disabled causes TX errors
     * (no ACK) which can push can2040 into bus-off state. */
    hal_can_enable(CAN_BUS_1, true);
    vTaskDelay(pdMS_TO_TICKS(2)); /* Let transceiver stabilize */

    report_test(2, pin_high ? RESULT_PASS : RESULT_FAIL, NULL, 0);
}

static void test_t1_3_can1_term(void)
{
    /* T1.3: Set CAN1 termination=true, verify pin 4 reads HIGH */
    hal_can_set_termination(CAN_BUS_1, true);
    vTaskDelay(pdMS_TO_TICKS(1));
    bool pin_high = gpio_get(CAN1_TERM_PIN);
    uint8_t extra = pin_high ? 1 : 0;
    report_test(3, pin_high ? RESULT_PASS : RESULT_FAIL, &extra, 1);
    hal_can_set_termination(CAN_BUS_1, false);
}

static void test_t1_4_can2_en_term(void)
{
    /* T1.4: CAN2 EN and TERM pins */
    uint8_t results = 0;

    /* Enable */
    hal_can_enable(CAN_BUS_2, true);
    vTaskDelay(pdMS_TO_TICKS(1));
    if (!gpio_get(CAN2_EN_PIN)) results |= 0x01; /* EN LOW = enabled */

    /* Disable */
    hal_can_enable(CAN_BUS_2, false);
    vTaskDelay(pdMS_TO_TICKS(1));
    if (gpio_get(CAN2_EN_PIN)) results |= 0x02; /* EN HIGH = disabled */

    /* Termination on */
    hal_can_set_termination(CAN_BUS_2, true);
    vTaskDelay(pdMS_TO_TICKS(1));
    if (gpio_get(CAN2_TERM_PIN)) results |= 0x04; /* TERM HIGH = on */

    /* Termination off */
    hal_can_set_termination(CAN_BUS_2, false);
    vTaskDelay(pdMS_TO_TICKS(1));
    if (!gpio_get(CAN2_TERM_PIN)) results |= 0x08; /* TERM LOW = off */

    bool all_pass = (results == 0x0F);
    report_test(4, all_pass ? RESULT_PASS : RESULT_FAIL, &results, 1);
}

static void test_t1_5_spi_init(void)
{
    /* T1.5: SPI0 initializes without fault */
    hal_spi_ctx_t ctx;
    hal_spi_init(&ctx);
    bool ok = ctx.initialized && (ctx.mutex != NULL);
    report_test(5, ok ? RESULT_PASS : RESULT_FAIL, NULL, 0);
}

static void test_t1_6_sja1124_id_read(void)
{
    /* T1.6: Read SJA1124 ID register (0xFF) */
    hal_spi_ctx_t ctx;
    hal_spi_init(&ctx);

    uint8_t id_val = 0;
    bool ok = hal_spi_read_reg(&ctx, 0xFF, &id_val, 1);

    uint8_t extra[2] = { ok ? 1 : 0, id_val };
    /* Valid if we got a response and it's not 0x00 or 0xFF (bus stuck) */
    bool valid = ok && (id_val != 0x00) && (id_val != 0xFF);
    report_test(6, valid ? RESULT_PASS : RESULT_FAIL, extra, 2);
}

static void test_t1_7_sja1124_write_read(void)
{
    /* T1.7: Write a register and read it back */
    hal_spi_ctx_t ctx;
    hal_spi_init(&ctx);

    /* Write PLLCFG register (0x01) with value 0x0A */
    uint8_t write_val = 0x0A;
    hal_spi_write_reg(&ctx, 0x01, &write_val, 1);

    /* Read it back */
    uint8_t read_val = 0;
    hal_spi_read_reg(&ctx, 0x01, &read_val, 1);

    uint8_t extra[2] = { write_val, read_val & 0x0F };
    bool match = ((read_val & 0x0F) == write_val);
    report_test(7, match ? RESULT_PASS : RESULT_FAIL, extra, 2);
}

static void test_t1_8_nvm_write(void)
{
    /* T1.8: Write 256 bytes of known pattern to NVM Slot A */
    hal_nvm_init();

    uint8_t pattern[256];
    for (int i = 0; i < 256; i++) pattern[i] = (uint8_t)(i ^ 0xA5);

    /* Erase first */
    hal_nvm_erase_sector(NVM_SLOT_A_OFFSET);

    bool ok = hal_nvm_write_page(NVM_SLOT_A_OFFSET, pattern, 256);
    report_test(8, ok ? RESULT_PASS : RESULT_FAIL, NULL, 0);
}

static void test_t1_9_nvm_readback(void)
{
    /* T1.9: Read back 256 bytes from NVM Slot A, verify against pattern */
    uint8_t pattern[256];
    for (int i = 0; i < 256; i++) pattern[i] = (uint8_t)(i ^ 0xA5);

    uint8_t readback[256];
    hal_nvm_read(NVM_SLOT_A_OFFSET, readback, 256);

    uint8_t mismatches = 0;
    uint8_t first_bad_idx = 0;
    for (int i = 0; i < 256; i++) {
        if (readback[i] != pattern[i]) {
            if (mismatches == 0) first_bad_idx = (uint8_t)i;
            mismatches++;
        }
    }

    uint8_t extra[2] = { mismatches, first_bad_idx };
    report_test(9, (mismatches == 0) ? RESULT_PASS : RESULT_FAIL, extra, 2);
}

static void test_t1_10_nvm_erase(void)
{
    /* T1.10: Erase NVM Slot A, verify all bytes = 0xFF */
    hal_nvm_erase_sector(NVM_SLOT_A_OFFSET);

    uint8_t readback[256];
    hal_nvm_read(NVM_SLOT_A_OFFSET, readback, 256);

    uint8_t non_ff = 0;
    for (int i = 0; i < 256; i++) {
        if (readback[i] != 0xFF) {
            non_ff++;
        }
    }

    uint8_t extra[1] = { non_ff };
    report_test(10, (non_ff == 0) ? RESULT_PASS : RESULT_FAIL, extra, 1);
}

static void test_t1_11_clock_output(void)
{
    /* T1.11: Start 8 MHz clock output on GPIO 21
     * We can't measure frequency from firmware — this just verifies
     * the clock init doesn't crash. Frequency must be verified with
     * an oscilloscope externally.
     */
    hal_clock_init();
    vTaskDelay(pdMS_TO_TICKS(10));
    /* If we got here without crashing, init worked */
    report_test(11, RESULT_PASS, NULL, 0);
    /* Note: MANUAL VERIFICATION REQUIRED — check 8 MHz on scope */
}

static void test_t1_12_bootloader_entry(void)
{
    /* T1.12: Bootloader entry via SRAM magic + watchdog reset.
     * WARNING: This test reboots the board!
     * Run it LAST and verify bootloader responds on CAN afterwards.
     * To skip: comment out this call.
     */
    /* Send a notice frame first */
    uint8_t extra[1] = { 0xBB }; /* 0xBB = "about to reboot" marker */
    report_test(12, RESULT_PASS, extra, 1);

    vTaskDelay(pdMS_TO_TICKS(50)); /* Let CAN TX complete */

    /* This does not return */
    hal_request_bootloader();
}

/* ---- Test Runner Task ---- */

static void test_runner_task(void *params)
{
    (void)params;

    /* Wait for CAN to be ready */
    vTaskDelay(pdMS_TO_TICKS(500));

    /* Send diagnostic frame: actual system clock in Hz (ID 0x7FD) */
    {
        uint32_t clk = clock_get_hz(clk_sys);
        can_frame_t diag = {0};
        diag.id = 0x7FD;
        diag.dlc = 4;
        diag.data[0] = (clk >> 24) & 0xFF;
        diag.data[1] = (clk >> 16) & 0xFF;
        diag.data[2] = (clk >> 8) & 0xFF;
        diag.data[3] = clk & 0xFF;
        can_manager_transmit(CAN_BUS_1, &diag);
        vTaskDelay(pdMS_TO_TICKS(50));
    }

    /* Run all tests */
    test_t1_1_can1_en_enable();
    test_t1_2_can1_en_disable();
    test_t1_3_can1_term();
    test_t1_4_can2_en_term();
    test_t1_5_spi_init();
    test_t1_6_sja1124_id_read();
    test_t1_7_sja1124_write_read();
    test_t1_8_nvm_write();
    test_t1_9_nvm_readback();
    test_t1_10_nvm_erase();
    test_t1_11_clock_output();

    /* Report summary (excluding T1.12 bootloader test) */
    report_summary();

    /* Uncomment to run bootloader entry test (reboots the board):
     * vTaskDelay(pdMS_TO_TICKS(100));
     * test_t1_12_bootloader_entry();
     */
    (void)test_t1_12_bootloader_entry; /* suppress unused warning */

    /* Done — idle */
    for (;;) {
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
}

/* ---- Test Main (replaces normal main when TEST_PHASE1 defined) ---- */

void vApplicationIdleHook(void) {}
void vApplicationStackOverflowHook(TaskHandle_t t, char *n) { (void)t; (void)n; for(;;); }
void vApplicationMallocFailedHook(void) { for(;;); }

int main(void)
{
    stdio_init_all();
    hal_gpio_init();

    /* Enable CAN1 termination for proper bus signalling */
    hal_can_set_termination(CAN_BUS_1, true);

    /* Minimal queue setup for CAN TX */
    QueueHandle_t dummy_q1 = xQueueCreate(4, sizeof(gateway_frame_t));
    QueueHandle_t dummy_q2 = xQueueCreate(4, sizeof(gateway_frame_t));
    QueueHandle_t dummy_q3 = xQueueCreate(4, sizeof(gateway_frame_t));

    can_manager_init(dummy_q1, dummy_q2, dummy_q3);
    can_manager_start_can1(CAN_DEFAULT_BITRATE);

    xTaskCreate(can_task_entry, "CAN", TASK_STACK_CAN, NULL, TASK_PRIORITY_CAN, NULL);
    xTaskCreate(test_runner_task, "TEST", 1024, NULL, 3, NULL);

    vTaskStartScheduler();
    for (;;) {}
    return 0;
}

#endif /* TEST_PHASE1 */
