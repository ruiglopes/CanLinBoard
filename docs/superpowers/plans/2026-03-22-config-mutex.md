# Config Read Mutex — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add FreeRTOS mutex to protect `s_working_config` from torn reads when CONFIG task writes while other tasks read.

**Architecture:** A single FreeRTOS mutex with priority inheritance guards all writes to `s_working_config`. Reader tasks take the mutex, copy fields to locals, release immediately. No changes to single-byte field reads (atomic on ARM Cortex-M33).

**Tech Stack:** C, FreeRTOS (RP2350_ARM_NTZ port), ARM Cortex-M33

**Spec:** `docs/superpowers/specs/2026-03-22-config-mutex-design.md`

---

## File Map

### Modified Files

| File | Change |
|------|--------|
| `firmware/src/config/config_handler.h` | Add `config_handler_lock()` / `config_handler_unlock()` declarations |
| `firmware/src/config/config_handler.c` | Add mutex, lock around writes in `handle_write_param`, `handle_bulk_end`, `handle_defaults` |
| `firmware/src/diag/diagnostics.c` | Lock + copy-to-locals in `diagnostics_task()` startup and main loop |
| `firmware/src/lin/lin_manager.c` | Lock + copy-to-locals at startup and PLL recovery |
| `firmware/src/main.c` | Lock + copy-to-locals in `gateway_task()` startup |

---

## Task 1: Add mutex and lock/unlock API to config_handler

**Files:**
- Modify: `firmware/src/config/config_handler.h`
- Modify: `firmware/src/config/config_handler.c`

- [ ] **Step 1: Add lock/unlock declarations to config_handler.h**

In `firmware/src/config/config_handler.h`, add before the `#endif`:

```c
/**
 * Lock the config mutex. Must be held while reading multi-byte fields
 * from the config returned by config_handler_get_config().
 * Copy fields to locals and unlock immediately — do NOT hold across
 * vTaskDelay() or any blocking call. Non-recursive.
 */
void config_handler_lock(void);
void config_handler_unlock(void);
```

Also add `#include "semphr.h"` after the existing `#include "queue.h"`.

- [ ] **Step 2: Add mutex static variable and init in config_handler.c**

In `firmware/src/config/config_handler.c`, add after `#include <string.h>` (line 19):

```c
#include "semphr.h"
```

Add after line 25 (`static QueueHandle_t s_can_tx_queue;`):

```c
static SemaphoreHandle_t s_config_mutex;
```

In `config_handler_init()` (line 724), add at the very top of the function body, before any other code:

```c
    s_config_mutex = xSemaphoreCreateMutex();
    configASSERT(s_config_mutex);
```

- [ ] **Step 3: Implement lock/unlock functions**

Add after `config_handler_get_config()` (after line 749):

```c
void config_handler_lock(void)
{
    xSemaphoreTake(s_config_mutex, portMAX_DELAY);
}

void config_handler_unlock(void)
{
    xSemaphoreGive(s_config_mutex);
}
```

- [ ] **Step 4: Build**

```bash
cd firmware && cmake --build build
```

Expected: 0 errors. Warnings about unused `s_config_mutex` are OK (used in next steps).

- [ ] **Step 5: Commit**

```bash
git add firmware/src/config/config_handler.h firmware/src/config/config_handler.c
git commit -m "Add config mutex and lock/unlock API"
```

---

## Task 2: Lock around writes in config_handler.c

**Files:**
- Modify: `firmware/src/config/config_handler.c`

- [ ] **Step 1: Lock around handle_write_param**

In `handle_write_param()` (line 356), the writes to `s_working_config` happen in the switch cases (lines 378-468). Wrap the entire switch block — add lock before the `switch (section)` at line 368, and unlock before the final `send_response` at line 476:

After the local variable declarations (line 365 `uint8_t sub = data[3];`) and before `switch (section) {` (line 368), add:

```c
    config_handler_lock();
```

The switch has early returns that `send_response` and `return` — these need unlock before return. However, to keep it simple, restructure: change all the inner `send_response(...); return;` cases to use a goto pattern, OR wrap at a finer granularity.

**Simplest approach**: lock/unlock around just the field assignments, not the entire switch. For each case that writes a multi-byte field, wrap just the assignment:

For `can[sub].bitrate` (line 378):
```c
            config_handler_lock();
            s_working_config.can[sub].bitrate = br;
            config_handler_unlock();
```

For `lin[sub].baudrate` (line 408):
```c
            config_handler_lock();
            s_working_config.lin[sub].baudrate = lbr;
            config_handler_unlock();
```

For `diag.can_id` (lines 421-423):
```c
            config_handler_lock();
            s_working_config.diag.can_id = (uint32_t)data[4] |
                                            ((uint32_t)data[5] << 8) |
                                            ((uint32_t)data[6] << 16);
            config_handler_unlock();
```

For `diag.interval_ms` (line 427):
```c
            config_handler_lock();
            s_working_config.diag.interval_ms = (uint16_t)data[4] | ((uint16_t)data[5] << 8);
            config_handler_unlock();
```

For `diag.can_watchdog_ms` (line 437):
```c
            config_handler_lock();
            s_working_config.diag.can_watchdog_ms = (uint16_t)data[4] | ((uint16_t)data[5] << 8);
            config_handler_unlock();
```

For `diag.lin_watchdog_ms` (line 441):
```c
            config_handler_lock();
            s_working_config.diag.lin_watchdog_ms = (uint16_t)data[4] | ((uint16_t)data[5] << 8);
            config_handler_unlock();
```

Single-byte writes (`termination`, `enabled`, `mode`, `bus`, `bulk_tx_retries`, `bulk_tx_retry_delay_ms`, profile fields) are atomic on ARM — no lock needed.

- [ ] **Step 2: Lock around handle_bulk_end**

In `handle_bulk_end()` (line 533), the writes happen at lines 557-559 (routing rules) and 572-575 (LIN schedule). Wrap each:

For routing rules (lines 555-559):
```c
        config_handler_lock();
        s_working_config.routing_rule_count = count;
        memcpy(s_working_config.routing_rules, s_bulk_buffer,
               count * sizeof(routing_rule_t));
        config_handler_unlock();
```

For LIN schedule (lines 571-575):
```c
        config_handler_lock();
        memset(&s_working_config.lin[s_bulk_sub].schedule, 0,
               sizeof(lin_schedule_table_t));
        memcpy(&s_working_config.lin[s_bulk_sub].schedule, s_bulk_buffer,
               s_bulk_received);
        config_handler_unlock();
```

- [ ] **Step 3: Lock around handle_defaults**

In `handle_defaults()` (line 138), wrap the `nvm_config_defaults` call:

```c
static void handle_defaults(void)
{
    config_handler_lock();
    nvm_config_defaults(&s_working_config);
    config_handler_unlock();
    send_response(CFG_CMD_DEFAULTS, CFG_STATUS_OK, NULL, 0);
}
```

- [ ] **Step 4: Build**

```bash
cd firmware && cmake --build build
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add firmware/src/config/config_handler.c
git commit -m "Lock config mutex around all multi-byte writes to s_working_config"
```

---

## Task 3: Add locking to diagnostics_task readers

**Files:**
- Modify: `firmware/src/diag/diagnostics.c`

- [ ] **Step 1: Add include**

Add at the top of `diagnostics.c`, after the existing includes:

```c
#include "config/config_handler.h"
```

Check if it's already included — it likely is since `config_handler_get_config()` is called. If already present, skip.

- [ ] **Step 2: Lock startup reads (lines 203-265)**

In `diagnostics_task()`, after `cfg = config_handler_get_config();` (line 203), the startup code reads `cfg->diag.can_watchdog_ms`, `cfg->diag.lin_watchdog_ms`, `cfg->diag.can_id`, `cfg->diag.bus`. Replace the raw pointer reads with locked copies.

Replace lines 203-206 with:

```c
    const nvm_config_t *cfg = config_handler_get_config();

    /* Copy startup config under lock */
    config_handler_lock();
    uint16_t init_can_wd_ms  = cfg->diag.can_watchdog_ms;
    uint16_t init_lin_wd_ms  = cfg->diag.lin_watchdog_ms;
    uint32_t init_can_id     = cfg->diag.can_id;
    uint8_t  init_bus        = cfg->diag.bus;
    config_handler_unlock();

    bus_watchdog_init(init_can_wd_ms, init_lin_wd_ms);
```

Then update the version frame and crash report sections that use `cfg->diag.can_id` and `cfg->diag.bus` to use `init_can_id` and `init_bus` instead. Find the exact lines that reference these fields in the one-shot startup frames and replace.

- [ ] **Step 3: Lock main loop reads (lines 271-296)**

The main loop re-reads config every iteration and passes `cfg` directly to `send_heartbeat()`. Replace with copy-to-locals pattern:

Replace lines 272-289 with:

```c
        /* Copy diag config under lock */
        config_handler_lock();
        cfg = config_handler_get_config();
        uint32_t hb_can_id     = cfg->diag.can_id;
        uint16_t hb_interval   = cfg->diag.interval_ms;
        uint8_t  hb_bus        = cfg->diag.bus;
        bool     hb_enabled    = cfg->diag.enabled;
        config_handler_unlock();

        if (!hb_enabled || hb_interval == 0) {
            vTaskDelay(pdMS_TO_TICKS(1000));
            state_check_counter++;
            if (state_check_counter >= 10) {
                state_check_counter = 0;
                update_system_state();
            }
            uptime_s++;
            continue;
        }

        vTaskDelay(pdMS_TO_TICKS(hb_interval));
        uptime_s += (hb_interval + 500) / 1000;

        send_heartbeat(cfg, uptime_s);
```

**IMPORTANT**: `send_heartbeat(cfg, uptime_s)` still passes the raw pointer. The function reads `cfg->diag.can_id` and `cfg->diag.bus` — these were already copied above but `send_heartbeat` doesn't use the copies. Two options:

**Option A (minimal change)**: Change `send_heartbeat` signature to accept `can_id` and `bus` as parameters instead of the full config pointer. This requires modifying the function signature and all heartbeat frame construction.

**Option B (simpler)**: Since `send_heartbeat` runs without yielding (it just constructs and sends 4 CAN frames synchronously), the risk of a torn read during the ~100us of send_heartbeat execution is extremely low. But for correctness, refactor `send_heartbeat` to take `uint32_t base_id, can_bus_id_t tx_bus` instead of `const nvm_config_t *cfg`.

Use **Option B**: Change `send_heartbeat` signature:

```c
static void send_heartbeat(uint32_t base_id, can_bus_id_t tx_bus, uint32_t uptime_s)
```

Update the function body to use `base_id` and `tx_bus` directly (remove lines 81-82 that extracted these from cfg). Update the call site:

```c
        can_bus_id_t tx_bus = (hb_bus == 0) ? CAN_BUS_1 : CAN_BUS_2;
        send_heartbeat(hb_can_id, tx_bus, uptime_s);
```

- [ ] **Step 4: Build**

```bash
cd firmware && cmake --build build
```

Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add firmware/src/diag/diagnostics.c
git commit -m "Add config mutex locking to diagnostics_task reads"
```

---

## Task 4: Add locking to lin_manager readers

**Files:**
- Modify: `firmware/src/lin/lin_manager.c`

- [ ] **Step 1: Lock startup config read (lines 297-309)**

In `lin_task_entry()`, the config is read at line 297 and used to start channels. Wrap the config read block. The pattern reads `cfg->lin[ch].enabled`, `.mode`, `.baudrate`, `.schedule` — the last two are multi-byte.

Replace lines 297-309 with a pattern that locks, copies each channel's config to a local `lin_channel_config_t` + schedule, unlocks, then starts channels:

```c
        const nvm_config_t *cfg = config_handler_get_config();
        for (int ch = 0; ch < LIN_CHANNEL_COUNT; ch++) {
            if (cfg->lin[ch].enabled) {
                lin_channel_config_t lc;
                lin_schedule_table_t sched;

                config_handler_lock();
                lc.enabled  = true;
                lc.mode     = (lin_mode_t)cfg->lin[ch].mode;
                lc.baudrate = cfg->lin[ch].baudrate;
                memcpy(&sched, &cfg->lin[ch].schedule, sizeof(lin_schedule_table_t));
                config_handler_unlock();

                lin_manager_start_channel(ch, &lc, &sched);
            }
        }
```

- [ ] **Step 2: Lock PLL recovery config read (lines 339-350)**

Same pattern as startup — this is in the PLL recovery path. Apply identical lock/copy/unlock around the loop body.

- [ ] **Step 3: Build**

```bash
cd firmware && cmake --build build
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add firmware/src/lin/lin_manager.c
git commit -m "Add config mutex locking to lin_task_entry config reads"
```

---

## Task 5: Add locking to gateway_task reader

**Files:**
- Modify: `firmware/src/main.c`

- [ ] **Step 1: Lock gateway startup config read (lines 49-54)**

In `gateway_task()`, the routing rules are read from config at startup:

```c
    const nvm_config_t *cfg = config_handler_get_config();
    for (int i = 0; i < cfg->routing_rule_count && i < MAX_ROUTING_RULES; i++) {
        routing_rule_t rule;
        memcpy(&rule, &cfg->routing_rules[i], sizeof(routing_rule_t));
        gateway_engine_add_rule(&rule);
    }
```

Wrap with lock:

```c
    config_handler_lock();
    const nvm_config_t *cfg = config_handler_get_config();
    uint8_t rule_count = cfg->routing_rule_count;
    routing_rule_t rules[MAX_ROUTING_RULES];
    for (int i = 0; i < rule_count && i < MAX_ROUTING_RULES; i++) {
        memcpy(&rules[i], &cfg->routing_rules[i], sizeof(routing_rule_t));
    }
    config_handler_unlock();

    for (int i = 0; i < rule_count && i < MAX_ROUTING_RULES; i++) {
        gateway_engine_add_rule(&rules[i]);
    }
```

Note: `routing_rule_t rules[MAX_ROUTING_RULES]` is 64 * 32 = 2048 bytes on stack. `TASK_STACK_GATEWAY` is 1024 words (4096 bytes), so this fits. If concerned, copy one rule at a time under lock:

```c
    config_handler_lock();
    const nvm_config_t *cfg = config_handler_get_config();
    uint8_t rule_count = cfg->routing_rule_count;
    config_handler_unlock();

    for (int i = 0; i < rule_count && i < MAX_ROUTING_RULES; i++) {
        routing_rule_t rule;
        config_handler_lock();
        memcpy(&rule, &cfg->routing_rules[i], sizeof(routing_rule_t));
        config_handler_unlock();
        gateway_engine_add_rule(&rule);
    }
```

Use the second approach (one rule at a time) — safer on stack, still correct.

- [ ] **Step 2: Build**

```bash
cd firmware && cmake --build build
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add firmware/src/main.c
git commit -m "Add config mutex locking to gateway_task startup config read"
```

---

## Task 6: Build verification and on-target test

- [ ] **Step 1: Clean build**

```bash
cd firmware && cmake --build build --clean-first
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Build all test targets**

```bash
cd firmware && cmake --build build --target test_phase5
cd firmware && cmake --build build --target test_phase6
```

Expected: both build successfully.

- [ ] **Step 3: Flash and run Phase 5 tests**

Phase 5 tests exercise config writes (WRITE_PARAM, BULK_START/END, SAVE, DEFAULTS) which now take the mutex. Verify all tests still pass.

- [ ] **Step 4: Flash and run Phase 6 tests**

Phase 6 tests exercise diagnostics (heartbeat frames, bus watchdogs) which now read config under lock. Verify all tests still pass.

- [ ] **Step 5: Manual concurrency test**

With the board running production firmware:
1. Connect config tool
2. Start monitoring diagnostics frames (heartbeat on 0x7F0)
3. Rapidly change `diag.can_id` via Write All + Save (change and save 5+ times in quick succession)
4. Verify heartbeat frames always use a valid CAN ID (no torn values, no frames on unexpected IDs)

- [ ] **Step 6: Final commit if any fixes needed**

```bash
git add -u
git commit -m "Fix issues found during config mutex testing"
```
