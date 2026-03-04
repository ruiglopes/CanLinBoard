#ifndef BUS_WATCHDOG_H
#define BUS_WATCHDOG_H

#include <stdint.h>
#include <stdbool.h>
#include "can/can_bus.h"  /* bus_id_t, BUS_COUNT */

/**
 * Initialize the bus watchdog module.
 * Creates 6 auto-reload FreeRTOS timers (CAN1, CAN2, LIN1-4).
 * Timers are NOT started — call bus_watchdog_set_enabled() to start.
 *
 * @param can_timeout_ms  Timeout for CAN buses (0 = disabled)
 * @param lin_timeout_ms  Timeout for LIN buses (0 = disabled)
 */
void bus_watchdog_init(uint16_t can_timeout_ms, uint16_t lin_timeout_ms);

/**
 * Feed (reset) the watchdog for a specific bus.
 * Must be called from task context only.
 * Clears the timeout flag and resets the timer.
 */
void bus_watchdog_feed(bus_id_t bus);

/**
 * Check if a bus has timed out.
 * Sticky flag — remains true until bus_watchdog_feed() clears it.
 */
bool bus_watchdog_timed_out(bus_id_t bus);

/**
 * Get a bitmask of all timed-out buses.
 * bit0=CAN1, bit1=CAN2, bit2=LIN1, ..., bit5=LIN4
 */
uint8_t bus_watchdog_get_timeout_mask(void);

/**
 * Enable or disable the watchdog for a specific bus.
 * Starting resets the timer; stopping clears the timeout flag.
 */
void bus_watchdog_set_enabled(bus_id_t bus, bool enabled);

/**
 * Reconfigure timeouts at runtime.
 * Stops all timers, changes periods, restarts those that were enabled.
 */
void bus_watchdog_reconfigure(uint16_t can_timeout_ms, uint16_t lin_timeout_ms);

#endif /* BUS_WATCHDOG_H */
