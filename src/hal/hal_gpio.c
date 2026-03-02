#include "hal/hal_gpio.h"
#include "board_config.h"
#include "hardware/gpio.h"
#include "hardware/watchdog.h"

/* Pin tables for CAN buses indexed by can_bus_id_t */
static const uint8_t can_en_pins[]   = { CAN1_EN_PIN,   CAN2_EN_PIN };
static const uint8_t can_term_pins[] = { CAN1_TERM_PIN, CAN2_TERM_PIN };

void hal_gpio_init(void)
{
    /* ---- CAN1 EN (active-low) — start disabled (HIGH) ---- */
    gpio_init(CAN1_EN_PIN);
    gpio_set_dir(CAN1_EN_PIN, GPIO_OUT);
    gpio_put(CAN1_EN_PIN, 1);  /* Disabled */

    /* CAN1 TERM (active-high) — start off (LOW) */
    gpio_init(CAN1_TERM_PIN);
    gpio_set_dir(CAN1_TERM_PIN, GPIO_OUT);
    gpio_put(CAN1_TERM_PIN, 0);

    /* ---- CAN2 EN (active-low) — start disabled (HIGH) ---- */
    gpio_init(CAN2_EN_PIN);
    gpio_set_dir(CAN2_EN_PIN, GPIO_OUT);
    gpio_put(CAN2_EN_PIN, 1);  /* Disabled */

    /* CAN2 TERM (active-high) — start off (LOW) */
    gpio_init(CAN2_TERM_PIN);
    gpio_set_dir(CAN2_TERM_PIN, GPIO_OUT);
    gpio_put(CAN2_TERM_PIN, 0);

    /* ---- LIN INTN — input with pull-up (active-low open-drain) ---- */
    gpio_init(LIN_INT_PIN);
    gpio_set_dir(LIN_INT_PIN, GPIO_IN);
    gpio_pull_up(LIN_INT_PIN);

    /* ---- LIN STAT — input ---- */
    gpio_init(LIN_STAT_PIN);
    gpio_set_dir(LIN_STAT_PIN, GPIO_IN);
}

void hal_can_enable(can_bus_id_t bus, bool enable)
{
    if (bus > CAN_BUS_2) return;
    /* EN is active-low: enable = true → pin LOW */
    gpio_put(can_en_pins[bus], !enable);
}

void hal_can_set_termination(can_bus_id_t bus, bool on)
{
    if (bus > CAN_BUS_2) return;
    /* TERM is active-high */
    gpio_put(can_term_pins[bus], on);
}

bool hal_can_is_enabled(can_bus_id_t bus)
{
    if (bus > CAN_BUS_2) return false;
    /* EN is active-low: pin LOW means enabled */
    return !gpio_get(can_en_pins[bus]);
}

bool hal_can_get_termination(can_bus_id_t bus)
{
    if (bus > CAN_BUS_2) return false;
    return gpio_get(can_term_pins[bus]);
}

bool hal_lin_int_active(void)
{
    /* INTN is active LOW */
    return !gpio_get(LIN_INT_PIN);
}

bool hal_lin_stat_active(void)
{
    return gpio_get(LIN_STAT_PIN);
}

void hal_request_bootloader(void)
{
    /* Write SRAM magic so bootloader stays in bootloader mode */
    volatile uint32_t *magic = (volatile uint32_t *)SRAM_MAGIC_ADDR;
    *magic = SRAM_MAGIC_VALUE;

    /* Trigger immediate watchdog reset */
    watchdog_reboot(0, 0, 0);

    /* Should never reach here */
    for (;;) {
        __breakpoint();
    }
}
