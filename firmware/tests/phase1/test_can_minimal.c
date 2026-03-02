/*
 * Minimal CAN test — NO FreeRTOS, bare-metal can2040.
 * Sends a heartbeat frame (ID 0x7FD) every ~500ms.
 * If you see frames on the bus, the hardware and can2040 work.
 *
 * Build:  cmake --build build --target test_can_minimal
 * Flash:  Copy test_can_minimal.uf2 via BOOTSEL
 */

#include "pico/stdlib.h"
#include "hardware/gpio.h"
#include "hardware/clocks.h"
#include "hardware/irq.h"
#include "can2040.h"

/* ---- Pin definitions (must match board_config.h) ---- */
#define CAN1_RX_PIN    1
#define CAN1_TX_PIN    2
#define CAN1_EN_PIN    3   /* Active LOW */
#define CAN1_TERM_PIN  4   /* Active HIGH */

static struct can2040 cbus;
static volatile bool tx_done = false;

static void can_callback(struct can2040 *cd, uint32_t notify, struct can2040_msg *msg)
{
    (void)cd;
    (void)msg;
    if (notify == CAN2040_NOTIFY_TX)
        tx_done = true;
}

static void pio0_irq_handler(void)
{
    can2040_pio_irq_handler(&cbus);
}

int main(void)
{
    stdio_init_all();

    /* Enable CAN1 transceiver */
    gpio_init(CAN1_EN_PIN);
    gpio_set_dir(CAN1_EN_PIN, GPIO_OUT);
    gpio_put(CAN1_EN_PIN, 0);  /* LOW = enabled */

    /* Enable termination */
    gpio_init(CAN1_TERM_PIN);
    gpio_set_dir(CAN1_TERM_PIN, GPIO_OUT);
    gpio_put(CAN1_TERM_PIN, 1); /* HIGH = terminated */

    /* Setup can2040 on PIO0 */
    can2040_setup(&cbus, 0);
    can2040_callback_config(&cbus, can_callback);

    /* PIO0 IRQ — use default priority (no FreeRTOS concerns) */
    irq_set_exclusive_handler(PIO0_IRQ_0, pio0_irq_handler);
    irq_set_enabled(PIO0_IRQ_0, true);

    /* Start CAN at 500 kbps */
    uint32_t sys_clock = clock_get_hz(clk_sys);
    can2040_start(&cbus, sys_clock, 500000, CAN1_RX_PIN, CAN1_TX_PIN);

    /* Heartbeat: send ID 0x7FD with counter every ~500ms */
    uint32_t counter = 0;
    for (;;) {
        struct can2040_msg msg = {0};
        msg.id = 0x7FD;
        msg.dlc = 8;
        msg.data[0] = 0xCA;  /* "CAN Alive" marker */
        msg.data[1] = 0xFE;
        msg.data[2] = (sys_clock >> 24) & 0xFF;
        msg.data[3] = (sys_clock >> 16) & 0xFF;
        msg.data[4] = (counter >> 24) & 0xFF;
        msg.data[5] = (counter >> 16) & 0xFF;
        msg.data[6] = (counter >> 8) & 0xFF;
        msg.data[7] = counter & 0xFF;

        tx_done = false;
        if (can2040_check_transmit(&cbus)) {
            can2040_transmit(&cbus, &msg);
            /* Wait for TX complete (with timeout) */
            for (int i = 0; i < 500000 && !tx_done; i++) {
                tight_loop_contents();
            }
        }

        counter++;
        busy_wait_ms(500);
    }

    return 0;
}
