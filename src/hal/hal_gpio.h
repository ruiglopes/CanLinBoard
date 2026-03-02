#ifndef HAL_GPIO_H
#define HAL_GPIO_H

#include <stdint.h>
#include <stdbool.h>

typedef enum {
    CAN_BUS_1 = 0,
    CAN_BUS_2 = 1,
} can_bus_id_t;

/**
 * Initialize all board GPIO pins (CAN EN/TERM, LIN control).
 * CAN transceivers start disabled, termination off.
 */
void hal_gpio_init(void);

/**
 * Enable/disable a CAN transceiver (EN pin is active-low).
 */
void hal_can_enable(can_bus_id_t bus, bool enable);

/**
 * Enable/disable CAN bus termination resistor (TERM pin is active-high).
 */
void hal_can_set_termination(can_bus_id_t bus, bool on);

/**
 * Read current CAN transceiver enable state.
 */
bool hal_can_is_enabled(can_bus_id_t bus);

/**
 * Read current CAN termination state.
 */
bool hal_can_get_termination(can_bus_id_t bus);

/**
 * Read the LIN INTN pin state (active LOW).
 * Returns true if interrupt is asserted (pin LOW).
 */
bool hal_lin_int_active(void);

/**
 * Read the LIN STAT pin state.
 */
bool hal_lin_stat_active(void);

/**
 * Write SRAM magic word and trigger watchdog reset to enter bootloader.
 * Does not return.
 */
void hal_request_bootloader(void) __attribute__((noreturn));

#endif /* HAL_GPIO_H */
