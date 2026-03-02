#ifndef HAL_CLOCK_H
#define HAL_CLOCK_H

#include <stdint.h>

/**
 * Initialize 8 MHz PWM clock output on LIN_CLOCK_PIN (GPIO 21).
 * This serves as the PLL reference clock for the SJA1124.
 */
void hal_clock_init(void);

/**
 * Stop the clock output.
 */
void hal_clock_stop(void);

#endif /* HAL_CLOCK_H */
