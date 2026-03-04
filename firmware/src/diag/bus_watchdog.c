#include "diag/bus_watchdog.h"
#include "FreeRTOS.h"
#include "timers.h"

#include <string.h>

/* ---- State ---- */
static TimerHandle_t s_timers[BUS_COUNT];
static volatile bool s_timed_out[BUS_COUNT];
static bool          s_enabled[BUS_COUNT];
static uint16_t      s_can_timeout_ms;
static uint16_t      s_lin_timeout_ms;

/* ---- Timer Callback ---- */

static void watchdog_timer_cb(TimerHandle_t xTimer)
{
    uint32_t bus = (uint32_t)(uintptr_t)pvTimerGetTimerID(xTimer);
    if (bus < BUS_COUNT) {
        s_timed_out[bus] = true;
    }
}

/* ---- Helpers ---- */

static uint16_t timeout_for_bus(bus_id_t bus)
{
    if (bus <= BUS_CAN2) return s_can_timeout_ms;
    return s_lin_timeout_ms;
}

/* ---- Public API ---- */

void bus_watchdog_init(uint16_t can_timeout_ms, uint16_t lin_timeout_ms)
{
    s_can_timeout_ms = can_timeout_ms;
    s_lin_timeout_ms = lin_timeout_ms;
    memset((void *)s_timed_out, 0, sizeof(s_timed_out));
    memset(s_enabled, 0, sizeof(s_enabled));

    static const char *names[BUS_COUNT] = {
        "wdt_c1", "wdt_c2", "wdt_l1", "wdt_l2", "wdt_l3", "wdt_l4"
    };

    for (int i = 0; i < BUS_COUNT; i++) {
        uint16_t ms = timeout_for_bus((bus_id_t)i);
        /* Use a minimum period of 100ms if timeout is 0 (timer won't be started) */
        TickType_t period = pdMS_TO_TICKS(ms > 0 ? ms : 100);
        if (period == 0) period = 1;

        s_timers[i] = xTimerCreate(
            names[i],
            period,
            pdTRUE,  /* auto-reload */
            (void *)(uintptr_t)i,
            watchdog_timer_cb
        );
    }
}

void bus_watchdog_feed(bus_id_t bus)
{
    if (bus >= BUS_COUNT) return;
    if (!s_enabled[bus]) return;
    if (!s_timers[bus]) return;

    s_timed_out[bus] = false;
    xTimerReset(s_timers[bus], 0);
}

bool bus_watchdog_timed_out(bus_id_t bus)
{
    if (bus >= BUS_COUNT) return false;
    return s_timed_out[bus];
}

uint8_t bus_watchdog_get_timeout_mask(void)
{
    uint8_t mask = 0;
    for (int i = 0; i < BUS_COUNT; i++) {
        if (s_timed_out[i]) mask |= (1U << i);
    }
    return mask;
}

void bus_watchdog_set_enabled(bus_id_t bus, bool enabled)
{
    if (bus >= BUS_COUNT) return;
    if (!s_timers[bus]) return;

    uint16_t ms = timeout_for_bus(bus);

    if (enabled && ms > 0) {
        s_timed_out[bus] = false;
        s_enabled[bus] = true;
        xTimerReset(s_timers[bus], 0);
    } else {
        s_enabled[bus] = false;
        xTimerStop(s_timers[bus], 0);
        s_timed_out[bus] = false;
    }
}

void bus_watchdog_reconfigure(uint16_t can_timeout_ms, uint16_t lin_timeout_ms)
{
    s_can_timeout_ms = can_timeout_ms;
    s_lin_timeout_ms = lin_timeout_ms;

    for (int i = 0; i < BUS_COUNT; i++) {
        if (!s_timers[i]) continue;

        bool was_enabled = s_enabled[i];

        /* Stop timer first */
        if (was_enabled) {
            xTimerStop(s_timers[i], 0);
        }

        uint16_t ms = timeout_for_bus((bus_id_t)i);
        if (ms > 0) {
            TickType_t period = pdMS_TO_TICKS(ms);
            if (period == 0) period = 1;
            xTimerChangePeriod(s_timers[i], period, 0);

            if (was_enabled) {
                s_timed_out[i] = false;
                xTimerReset(s_timers[i], 0);
            }
        } else if (was_enabled) {
            s_enabled[i] = false;
            s_timed_out[i] = false;
        }
    }
}
