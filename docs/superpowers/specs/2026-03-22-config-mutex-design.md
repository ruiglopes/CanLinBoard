# Config Read Mutex — Design Spec

Fix P1.1: config reads across tasks without synchronization.

## Problem

`config_handler_get_config()` returns a pointer to `s_working_config` that is read by 4 tasks while the CONFIG task modifies it. Multi-byte fields (uint16, uint32, structs) can be read in a torn state during concurrent writes.

## Solution

Add a FreeRTOS mutex with priority inheritance to protect `s_working_config`.

### Writer side (CONFIG task only)

Lock around all writes to `s_working_config`:
- `handle_write_param()` — individual field writes
- `handle_bulk_end()` — memcpy of routing rules and LIN schedules
- `handle_defaults()` — `nvm_config_defaults()` fills entire struct

### Reader side

New API: `config_handler_lock()` / `config_handler_unlock()`. Readers that access multi-byte fields wrap their reads, copy to locals, and unlock immediately.

`config_handler_get_config()` remains unchanged — returns `const nvm_config_t *`.

### Which readers need locking

| Location | Task | Priority | Fields | Needs lock |
|----------|------|----------|--------|------------|
| `diagnostics.c` — `diagnostics_task()` startup | DIAG | 1 | `can_id` (u32), `bus` (u8), `can_watchdog_ms` (u16), `lin_watchdog_ms` (u16) | Yes — multi-byte fields used in version frame and watchdog init |
| `diagnostics.c` — `diagnostics_task()` loop | DIAG | 1 | `can_id` (u32), `interval_ms` (u16), `bus` (u8), `enabled` (u8) | Yes — all four must be copied under one lock per heartbeat cycle |
| `diagnostics.c` — `diagnostics_reconfigure()` | CONFIG | 2 | `can_watchdog_ms` (u16), `lin_watchdog_ms` (u16) | No — same task as writer |
| `config_handler.c` — `handle_read_param()`, `handle_connect()` | CONFIG | 2 | Various | No — same task as writer |
| `lin_manager.c` — `lin_task_entry()` startup | LIN | 4 | `baudrate` (u32), `schedule` (258B memcpy) | Yes — multi-byte + struct copy |
| `lin_manager.c` — PLL recovery | LIN | 4 | Same as startup | Yes |
| `main.c` — `gateway_task()` startup | GATEWAY | 3 | `routing_rule_count` (u8) + `routing_rules[]` (64B x N memcpy) | Yes — array copy |
| `main.c` — `main()` boot | — | — | `can[]`, `lin[]` | No — before scheduler |

### Mutex type

`xSemaphoreCreateMutex()` — FreeRTOS mutex with automatic priority inheritance.

If a high-priority reader (GATEWAY at 3, LIN at 4) blocks while CONFIG (priority 2) holds the mutex, CONFIG is temporarily boosted to the reader's priority, preventing priority inversion.

### API

```c
// config_handler.h — new declarations
void config_handler_lock(void);
void config_handler_unlock(void);

// config_handler.c — implementation
static SemaphoreHandle_t s_config_mutex;

void config_handler_init(void)
{
    s_config_mutex = xSemaphoreCreateMutex();
    configASSERT(s_config_mutex);
    // ... existing init ...
}

void config_handler_lock(void)
{
    xSemaphoreTake(s_config_mutex, portMAX_DELAY);
}

void config_handler_unlock(void)
{
    xSemaphoreGive(s_config_mutex);
}
```

### Reader pattern

Copy **all** fields needed for a complete operation under one lock acquisition:

```c
// diagnostics_task() heartbeat cycle — copy all diag fields at once
config_handler_lock();
const nvm_config_t *cfg = config_handler_get_config();
uint32_t local_can_id = cfg->diag.can_id;
uint16_t local_interval = cfg->diag.interval_ms;
uint8_t  local_bus = cfg->diag.bus;
bool     local_enabled = cfg->diag.enabled;
config_handler_unlock();
// Use local copies for the entire heartbeat cycle (including send_heartbeat)
```

The lock scope must cover all fields used until the next lock — do NOT hold the raw `cfg` pointer across `vTaskDelay()` or function calls that may yield.

### Writer pattern

```c
static void handle_write_param(const uint8_t *data, uint8_t dlc)
{
    // ... validation ...
    config_handler_lock();
    s_working_config.diag.can_id = new_value;
    config_handler_unlock();
    send_response(...);
}
```

### Constraints

- **Non-recursive**: `config_handler_lock()` must NOT be called recursively — the mutex is non-recursive (`configUSE_RECURSIVE_MUTEXES = 0`). This is safe because no writer function calls another writer function, and reader tasks don't call writer functions.
- **No lock during yield**: Never hold the config mutex across `vTaskDelay()`, `xQueueReceive()`, or any blocking call.

### What is NOT changed

- `gateway_engine_replace_rules()` — has its own `taskENTER_CRITICAL()` for the rule table swap (separate data structure)
- `apply_config()` — runs in CONFIG task, internal to the writer. Passes `cfg` pointer to subsystem functions that may yield, but since CONFIG is the only writer, no other task can modify the config during these yields
- Single-byte field reads without locking — atomic on ARM Cortex-M33
- `config_handler_get_config()` — still returns pointer, readers choose whether to lock

### Files modified

| File | Change |
|------|--------|
| `firmware/src/config/config_handler.c` | Add mutex, lock/unlock around writes in `handle_write_param`, `handle_bulk_end`, `handle_defaults` |
| `firmware/src/config/config_handler.h` | Add `config_handler_lock()` / `config_handler_unlock()` declarations |
| `firmware/src/diag/diagnostics.c` | Lock around multi-byte config reads in `diagnostics_task()` loop |
| `firmware/src/lin/lin_manager.c` | Lock around config reads at startup and PLL recovery |
| `firmware/src/main.c` | Lock around config reads in `gateway_task()` startup |

### Testing

- Build verification (Phase 0)
- On-target: existing Phase 5/6 tests exercise config writes + diagnostics reads concurrently
- Manual: rapid config parameter changes via config tool while monitoring diagnostics — verify no torn values in heartbeat frames
