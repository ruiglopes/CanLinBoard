#include "hal/hal_clock.h"
#include "board_config.h"
#include "hardware/pwm.h"
#include "hardware/gpio.h"
#include "hardware/clocks.h"

/*
 * Generate 8 MHz clock on GPIO 21 using PWM.
 *
 * With SYS_CLK = 150 MHz:
 *   PWM wrap = (150 MHz / 8 MHz) - 1 = 17.75 → not integer
 *
 * Alternative: use clock_gpio_init() to output a divided system clock.
 * 150 MHz / 8 MHz = 18.75 — also not exact.
 *
 * Best approach: Configure a PLL or use the GPOUT clock divider.
 * The RP2350 GPOUT can divide the 48 MHz USB PLL:
 *   48 MHz / 6 = 8 MHz exactly.
 *
 * We'll use CLOCKS_CLK_GPOUT0 mapped to GPIO 21.
 */

static uint pwm_slice;

void hal_clock_init(void)
{
    /*
     * Use clock_gpio_init to route a divided clock to GPIO 21.
     * Source: clk_usb (48 MHz) with integer divisor 6 = 8 MHz.
     */
    clock_gpio_init(LIN_CLOCK_PIN, CLOCKS_CLK_GPOUT0_CTRL_AUXSRC_VALUE_CLK_USB, 6);
}

void hal_clock_stop(void)
{
    /* Disable the GPOUT clock by de-initializing the pin */
    gpio_init(LIN_CLOCK_PIN);
    gpio_set_dir(LIN_CLOCK_PIN, GPIO_OUT);
    gpio_put(LIN_CLOCK_PIN, 0);
}
