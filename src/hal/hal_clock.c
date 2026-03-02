#include "hal/hal_clock.h"
#include "board_config.h"
#include "hardware/gpio.h"
#include "hardware/clocks.h"

/*
 * Generate 8 MHz clock on GPIO 21.
 *
 * Use clock_gpio_init() to route the GPOUT clock divider to GPIO 21.
 * Source: 48 MHz USB PLL / 6 = 8 MHz exactly.
 */

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
