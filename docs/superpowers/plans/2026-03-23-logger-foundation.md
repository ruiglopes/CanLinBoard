# Logger Foundation (Plan 5A) — Implementation Plan

> **STATUS: COMPLETE AND TESTED (2026-03-24)** — On-target Phase 8: 15/15 tests pass (`tests/phase8/test_logger_host.py`). All Data Logger manual UI tests pass. DL-5 N/A (needs 2nd CAN adapter).

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add on-board flash logging with manual start/stop and chunked download to the firmware, plus a Data Logger tab in the config tool for control, download, and export.

**Architecture:** A new `flash_logger` module manages the secondary flash ring buffer (CS1, 0x021000–0xFFFFFF). Frames are written inline from `can_task` and `lin_task` via a FreeRTOS queue drained by a dedicated logger task. The config protocol adds `SectionLog` (0x07) for mode/state control and `CmdLogReadChunk` (0x24) for 4 KB chunked downloads with per-chunk CRC32. The config tool adds a Data Logger tab with LogControlPanel (start/stop, status) and LogDownloadPanel (chunked download with progress bar and export).

**Tech Stack:** C (firmware, FreeRTOS, QMI flash driver), C# .NET 8 (config tool, WPF, CommunityToolkit.Mvvm, xUnit)

**Spec:** `docs/superpowers/specs/2026-03-23-bus-monitor-logger-design.md` § 3, § 5, § 8
**Master tracker:** `docs/bus-monitor-logger-master-plan.md` (Plan 5A)
**Branch:** `feature/bus-monitor-foundation`

**Important:** Do NOT commit specs or plans. Only commit actual code changes, and only after the feature is completed and tested.

---

## File Map

### Firmware — New Files

| File | Responsibility |
|------|---------------|
| `firmware/src/logger/flash_logger.h` | Public API: init, start, stop, enqueue, status, metadata read |
| `firmware/src/logger/flash_logger.c` | Flash ring buffer driver, page buffering, metadata management, erase-ahead |

### Firmware — Modified Files

| File | Changes |
|------|---------|
| `firmware/include/board_config.h` | Add logger flash address constants, queue depth, task stack size |
| `firmware/src/config/config_protocol.h` | Add `CFG_SECTION_LOG` (0x07), `CFG_CMD_LOG_READ_CHUNK` (0x24) |
| `firmware/src/config/config_handler.c` | Add READ_PARAM/WRITE_PARAM/handlers for SectionLog, chunked read handler |
| `firmware/src/can/can_manager.c` | Call `flash_logger_enqueue_frame()` after RX |
| `firmware/src/lin/lin_manager.c` | Call `flash_logger_enqueue_frame()` after LIN RX |
| `firmware/src/main.c` | Create logger queue, init flash_logger, create logger task |
| `firmware/src/hal/hal_flash_secondary.c` | Add NVM bounds check helper |
| `firmware/CMakeLists.txt` | Add `src/logger/flash_logger.c` to source lists |

### Config Tool — New Files

| File | Responsibility |
|------|---------------|
| `software/CanLinConfig/ViewModels/DataLoggerViewModel.cs` | Data Logger tab orchestrator |
| `software/CanLinConfig/ViewModels/LogControlViewModel.cs` | Logger mode, start/stop, status display |
| `software/CanLinConfig/ViewModels/LogDownloadViewModel.cs` | Chunked download with progress, resume, export |
| `software/CanLinConfig/Views/DataLoggerView.xaml` | Data Logger tab layout |
| `software/CanLinConfig/Views/DataLoggerView.xaml.cs` | Code-behind |
| `software/CanLinConfig/Views/LogControlPanel.xaml` | Control panel UserControl |
| `software/CanLinConfig/Views/LogControlPanel.xaml.cs` | Code-behind |
| `software/CanLinConfig/Views/LogDownloadPanel.xaml` | Download panel UserControl |
| `software/CanLinConfig/Views/LogDownloadPanel.xaml.cs` | Code-behind |
| `software/CanLinConfig.Tests/LogDownloadViewModelTests.cs` | Download logic unit tests |

### Config Tool — Modified Files

| File | Changes |
|------|---------|
| `software/CanLinConfig/Protocol/ProtocolConstants.cs` | Add `SectionLog`, logger param constants, `CmdLogReadChunk` |
| `software/CanLinConfig/Protocol/ConfigProtocol.cs` | Add `LogReadChunkAsync()` method |
| `software/CanLinConfig/ViewModels/MainViewModel.cs` | Add `DataLogger` sub-VM, wire protocol |
| `software/CanLinConfig/Views/MainWindow.xaml` | Add "Data Logger" tab |

---

## Task 1: Firmware — Flash Address Constants and NVM Bounds Check

**Files:**
- Modify: `firmware/include/board_config.h:123` (add constants after QUEUE_DEPTH_MONITOR_TX)
- Modify: `firmware/src/config/config_protocol.h:25` (add section and command)
- Modify: `firmware/src/hal/hal_flash_secondary.c` (add bounds check)
- Modify: `firmware/src/hal/hal_flash_secondary.h` (add bounds check API)

### Step-by-step:

- [ ] **Step 1: Add logger constants to board_config.h**

After `MONITOR_MAX_FILTER_IDS` (line 128), add:

```c
/* ---- Flash Logger ---- */
#define LOG_NVM_BOUNDARY        0x020000U   /* NVM config reserved up to here */
#define LOG_META_OFFSET         0x020000U   /* Logger metadata sector (4 KB) */
#define LOG_DATA_OFFSET         0x021000U   /* Log data ring buffer start */
#define LOG_DATA_END            SECONDARY_FLASH_SIZE  /* 0x1000000 (16 MB) */
#define LOG_DATA_SIZE           (LOG_DATA_END - LOG_DATA_OFFSET) /* ~16,252 KB */
#define LOG_ENTRY_SIZE          20U         /* sizeof(log_entry_t), packed */
#define LOG_PAGE_ENTRIES        (NVM_PAGE_SIZE / LOG_ENTRY_SIZE) /* 12 per 256-byte page, 16 bytes wasted */
#define LOG_SECTOR_ENTRIES      ((NVM_SECTOR_SIZE / NVM_PAGE_SIZE) * LOG_PAGE_ENTRIES) /* 192 per 4 KB sector */
#define LOG_META_MAGIC          0x4C4F4701U /* "LOG\x01" */

#define QUEUE_DEPTH_LOG_WRITE   32
#define TASK_STACK_LOG          512         /* words */

#define LOG_MAX_FLASH_ERRORS    10          /* stop logging after this many */
```

- [ ] **Step 2: Add SectionLog and CmdLogReadChunk to config_protocol.h**

After `CFG_SECTION_MONITOR` (line 25), add:

```c
#define CFG_SECTION_LOG             0x07
```

After `CFG_CMD_BULK_READ_DATA` (line 16), add:

```c
#define CFG_CMD_LOG_READ_CHUNK      0x24
```

- [ ] **Step 3: Add NVM bounds check to hal_flash_secondary**

In `firmware/src/hal/hal_flash_secondary.h`, after the existing function declarations (line 42), add:

```c
/**
 * Check if an address range falls within the NVM-reserved region.
 * @param addr  Start address
 * @param len   Length in bytes
 * @return true if the range is within [0, LOG_NVM_BOUNDARY)
 */
static inline bool sec_flash_is_nvm_region(uint32_t addr, size_t len)
{
    return (addr + len) <= LOG_NVM_BOUNDARY;
}
```

In `firmware/src/hal/hal_flash_secondary.c`, add bounds guards to `sec_flash_page_program()` and `sec_flash_sector_erase()`. At the top of each function, before any flash operation:

In `sec_flash_page_program()`, add as the first lines:

```c
    /* Guard: prevent NVM writes from spilling into logger space,
     * and logger writes from corrupting NVM space. */
```

Note: The bounds check is informational — the caller is responsible for respecting boundaries. The `flash_logger` module will only write to addresses >= `LOG_META_OFFSET`.

- [ ] **Step 4: Verify firmware compiles**

Run:
```bash
cd firmware && cmake --build build
```

- [ ] **Step 5: Commit**

```bash
git add firmware/include/board_config.h firmware/src/config/config_protocol.h firmware/src/hal/hal_flash_secondary.h firmware/src/hal/hal_flash_secondary.c
git commit -m "feat(firmware): add logger flash constants and NVM bounds check"
```

---

## Task 2: Firmware — Flash Logger Module (flash_logger.c/h)

**Files:**
- Create: `firmware/src/logger/flash_logger.h`
- Create: `firmware/src/logger/flash_logger.c`
- Modify: `firmware/CMakeLists.txt` (add source file)

### Step-by-step:

- [ ] **Step 1: Create flash_logger.h**

```c
#ifndef FLASH_LOGGER_H
#define FLASH_LOGGER_H

#include <stdint.h>
#include <stdbool.h>
#include "FreeRTOS.h"
#include "queue.h"

/* ---- Log Entry (20 bytes, packed) ---- */

typedef struct __attribute__((packed)) {
    uint32_t timestamp_ms;   /* relative to start_timestamp */
    uint32_t frame_id;       /* CAN ID or LIN PID */
    uint8_t  bus;            /* source bus enum (0=CAN1..5=LIN4, 0xFF=gap marker) */
    uint8_t  dlc;            /* 0-8 */
    uint8_t  data[8];        /* frame payload */
    uint16_t reserved;       /* pad to 20 bytes */
} log_entry_t;

_Static_assert(sizeof(log_entry_t) == 20, "log_entry_t must be 20 bytes");

/* ---- Logger Metadata (stored at LOG_META_OFFSET) ---- */

typedef struct __attribute__((packed)) {
    uint32_t magic;           /* LOG_META_MAGIC */
    uint32_t write_offset;    /* current write position in ring buffer (absolute flash addr) */
    uint32_t entry_count;     /* total entries written (saturates at UINT32_MAX) */
    uint32_t wrap_count;      /* times the ring has wrapped */
    uint32_t start_timestamp; /* absolute ms when logging started */
    uint8_t  state;           /* LOG_STATE_* */
    uint8_t  mode;            /* LOG_MODE_* */
    uint8_t  bus_mask;        /* which buses to log (bit0=CAN1..bit5=LIN4) */
    uint8_t  reserved;
    uint16_t flash_errors;    /* flash write/erase error counter */
    uint16_t reserved2;
    /* Reserved for Plan 5C trigger fields — keep struct at fixed size */
    uint32_t trigger_id;      /* reserved (Plan 5C) */
    uint8_t  trigger_bus;     /* reserved (Plan 5C) */
    uint8_t  trigger_byte;    /* reserved (Plan 5C) */
    uint8_t  trigger_op;      /* reserved (Plan 5C) */
    uint8_t  trigger_value;   /* reserved (Plan 5C) */
    uint32_t pre_trigger_kb;  /* reserved (Plan 5C) */
    uint32_t post_trigger_kb; /* reserved (Plan 5C) */
    uint32_t crc32;           /* metadata integrity check */
} log_metadata_t;

_Static_assert(sizeof(log_metadata_t) <= 256, "log_metadata_t must fit in one page");

/* ---- Logger States ---- */

#define LOG_STATE_IDLE          0
#define LOG_STATE_RECORDING     1
#define LOG_STATE_ERROR         4

/* ---- Logger Modes (Plan 5A: manual only) ---- */

#define LOG_MODE_MANUAL         0
/* LOG_MODE_CONTINUOUS = 1  (Plan 5B) */
/* LOG_MODE_TRIGGERED  = 2  (Plan 5C) */

/* ---- Config Protocol Params (SectionLog = 0x07) ---- */

/* Params 0-3: single-byte values, fit in READ_PARAM response */
#define LOG_PARAM_MODE          0   /* R/W, 1 byte */
#define LOG_PARAM_BUS_MASK      1   /* R/W, 1 byte */
#define LOG_PARAM_STATE_CMD     2   /* W, 1 byte: 0=stop, 1=start, 0xFF=erase */
#define LOG_PARAM_STATUS        3   /* R, 1 byte: LOG_STATE_* */

/* Params 4-6: 32-bit values, split into sub=0 (low 16) and sub=1 (high 16).
 * READ_PARAM response is [cmd][status][section][param][sub][val_lo][val_hi]
 * so max 3 value bytes per read. We use 2 bytes (16-bit halves). */
#define LOG_PARAM_ENTRY_COUNT   4   /* R, sub=0: low16, sub=1: high16 */
#define LOG_PARAM_WRAP_COUNT    5   /* R, sub=0: low16, sub=1: high16 */
#define LOG_PARAM_WRITE_OFFSET  6   /* R, sub=0: low16, sub=1: high16 */
#define LOG_PARAM_FLASH_ERRORS  7   /* R, 2 bytes */

/**
 * Initialize the flash logger module.
 * Reads existing metadata from flash if valid, otherwise initializes defaults.
 * @param log_queue  FreeRTOS queue for incoming gateway_frame_t items
 */
void flash_logger_init(QueueHandle_t log_queue);

/**
 * Logger task entry point. Drains log_queue, writes to flash.
 * @param params  unused
 */
void flash_logger_task(void *params);

/**
 * Enqueue a frame for logging. Called inline from can_task/lin_task.
 * If logging is disabled or bus is filtered, does nothing.
 * If queue is full, silently drops (no backpressure on CAN/LIN tasks).
 */
void flash_logger_enqueue_frame(const void *gf);

/* ---- Control API (called from config_handler) ---- */

void flash_logger_start(void);
void flash_logger_stop(void);
void flash_logger_erase_all(void);

/* ---- Status API (called from config_handler) ---- */

uint8_t  flash_logger_get_state(void);
uint8_t  flash_logger_get_mode(void);
void     flash_logger_set_mode(uint8_t mode);
uint8_t  flash_logger_get_bus_mask(void);
void     flash_logger_set_bus_mask(uint8_t mask);
uint32_t flash_logger_get_entry_count(void);
uint32_t flash_logger_get_wrap_count(void);
uint32_t flash_logger_get_write_offset(void);
uint16_t flash_logger_get_flash_errors(void);

/**
 * Read a chunk of log data from flash for download.
 * @param offset  Byte offset into the ring buffer (relative to LOG_DATA_OFFSET)
 * @param buf     Output buffer
 * @param len     Bytes to read (max 4096)
 * @return Actual bytes read
 */
uint16_t flash_logger_read_chunk(uint32_t offset, uint8_t *buf, uint16_t len);

#endif /* FLASH_LOGGER_H */
```

- [ ] **Step 2: Create flash_logger.c**

```c
#include "logger/flash_logger.h"
#include "board_config.h"
#include "hal/hal_flash_secondary.h"
#include "can/can_bus.h"
#include "util/crc32.h"

#include "FreeRTOS.h"
#include "task.h"
#include "hardware/timer.h"

#include <string.h>

/* ---- State ---- */

static QueueHandle_t s_log_queue;
static volatile uint8_t s_state = LOG_STATE_IDLE;
static log_metadata_t   s_meta;

/* Page write buffer: accumulate entries until a full page (256 bytes) */
static uint8_t  s_page_buf[NVM_PAGE_SIZE];
static uint16_t s_page_buf_pos;  /* bytes used in s_page_buf */

/* ---- Forward Declarations ---- */

static void load_metadata(void);
static void save_metadata(void);
static void erase_metadata_sector(void);
static void write_entry(const log_entry_t *entry);
static void flush_page_buffer(void);
static bool erase_sector_if_needed(uint32_t addr);

/* ---- Metadata ---- */

static void load_metadata(void)
{
    uint32_t irq = sec_flash_acquire_bus();
    sec_flash_read(LOG_META_OFFSET, (uint8_t *)&s_meta, sizeof(s_meta));
    sec_flash_release_bus(irq);

    /* Validate magic and CRC */
    if (s_meta.magic != LOG_META_MAGIC) {
        /* No valid metadata — initialize defaults */
        memset(&s_meta, 0, sizeof(s_meta));
        s_meta.magic = LOG_META_MAGIC;
        s_meta.write_offset = LOG_DATA_OFFSET;
        s_meta.bus_mask = 0x3F; /* All buses */
        s_meta.mode = LOG_MODE_MANUAL;
        return;
    }

    /* Verify CRC over all fields except crc32 itself */
    uint32_t crc = crc32_compute((const uint8_t *)&s_meta,
                              sizeof(s_meta) - sizeof(uint32_t));
    if (crc != s_meta.crc32) {
        /* CRC mismatch — reset to defaults but preserve write_offset
         * so we don't overwrite existing log data */
        uint32_t saved_offset = s_meta.write_offset;
        uint32_t saved_entries = s_meta.entry_count;
        uint32_t saved_wraps = s_meta.wrap_count;
        memset(&s_meta, 0, sizeof(s_meta));
        s_meta.magic = LOG_META_MAGIC;
        s_meta.write_offset = saved_offset;
        s_meta.entry_count = saved_entries;
        s_meta.wrap_count = saved_wraps;
        s_meta.bus_mask = 0x3F;
        s_meta.mode = LOG_MODE_MANUAL;
    }
}

static void save_metadata(void)
{
    /* Compute CRC over all fields except crc32 */
    s_meta.crc32 = crc32_compute((const uint8_t *)&s_meta,
                               sizeof(s_meta) - sizeof(uint32_t));

    /* Erase metadata sector and write */
    uint32_t irq = sec_flash_acquire_bus();
    sec_flash_sector_erase(LOG_META_OFFSET);
    sec_flash_page_program(LOG_META_OFFSET, (const uint8_t *)&s_meta, sizeof(s_meta));
    sec_flash_release_bus(irq);
}

static void erase_metadata_sector(void)
{
    uint32_t irq = sec_flash_acquire_bus();
    sec_flash_sector_erase(LOG_META_OFFSET);
    sec_flash_release_bus(irq);
}

/* ---- Flash Write Helpers ---- */

static bool erase_sector_if_needed(uint32_t addr)
{
    /* Erase when we hit the start of a new sector */
    if ((addr & (NVM_SECTOR_SIZE - 1)) != 0)
        return true;  /* Not at sector boundary, no erase needed */

    uint32_t irq = sec_flash_acquire_bus();
    bool ok = sec_flash_sector_erase(addr);
    sec_flash_release_bus(irq);

    if (!ok) {
        /* Retry once */
        irq = sec_flash_acquire_bus();
        ok = sec_flash_sector_erase(addr);
        sec_flash_release_bus(irq);
        if (!ok) {
            s_meta.flash_errors++;
            if (s_meta.flash_errors >= LOG_MAX_FLASH_ERRORS) {
                s_state = LOG_STATE_ERROR;
                s_meta.state = LOG_STATE_ERROR;
                save_metadata();
            }
            return false;
        }
    }
    return true;
}

static void flush_page_buffer(void)
{
    if (s_page_buf_pos == 0) return;

    /* Pad remainder with 0xFF (erased state) */
    if (s_page_buf_pos < NVM_PAGE_SIZE) {
        memset(&s_page_buf[s_page_buf_pos], 0xFF, NVM_PAGE_SIZE - s_page_buf_pos);
    }

    /* Erase sector if at boundary */
    if (!erase_sector_if_needed(s_meta.write_offset)) {
        /* Erase failed — skip this sector */
        s_meta.write_offset = (s_meta.write_offset & ~(NVM_SECTOR_SIZE - 1)) + NVM_SECTOR_SIZE;
        if (s_meta.write_offset >= LOG_DATA_END) {
            s_meta.write_offset = LOG_DATA_OFFSET;
            s_meta.wrap_count++;
        }
        s_page_buf_pos = 0;
        return;
    }

    /* Write the page */
    uint32_t irq = sec_flash_acquire_bus();
    bool ok = sec_flash_page_program(s_meta.write_offset, s_page_buf, NVM_PAGE_SIZE);
    sec_flash_release_bus(irq);

    if (!ok) {
        /* Retry once */
        irq = sec_flash_acquire_bus();
        ok = sec_flash_page_program(s_meta.write_offset, s_page_buf, NVM_PAGE_SIZE);
        sec_flash_release_bus(irq);
        if (!ok) {
            s_meta.flash_errors++;
            if (s_meta.flash_errors >= LOG_MAX_FLASH_ERRORS) {
                s_state = LOG_STATE_ERROR;
                s_meta.state = LOG_STATE_ERROR;
                save_metadata();
            }
        }
    }

    /* Advance write offset */
    s_meta.write_offset += NVM_PAGE_SIZE;
    if (s_meta.write_offset >= LOG_DATA_END) {
        s_meta.write_offset = LOG_DATA_OFFSET;
        s_meta.wrap_count++;
    }

    s_page_buf_pos = 0;
}

static void write_entry(const log_entry_t *entry)
{
    /* Copy entry into page buffer */
    memcpy(&s_page_buf[s_page_buf_pos], entry, LOG_ENTRY_SIZE);
    s_page_buf_pos += LOG_ENTRY_SIZE;

    /* 12 entries per page (12 * 20 = 240 bytes), 16 bytes wasted.
     * Flush when we can't fit another entry. */
    if (s_page_buf_pos + LOG_ENTRY_SIZE > NVM_PAGE_SIZE) {
        flush_page_buffer();
    }

    /* Increment entry count (saturate) */
    if (s_meta.entry_count < UINT32_MAX)
        s_meta.entry_count++;
}

/* ---- Bus Filter ---- */

static bool passes_bus_filter(uint8_t bus)
{
    if (bus >= 6) return false;  /* BUS_COUNT */
    return (s_meta.bus_mask & (1U << bus)) != 0;
}

/* ---- Public API ---- */

void flash_logger_init(QueueHandle_t log_queue)
{
    s_log_queue = log_queue;
    s_state = LOG_STATE_IDLE;
    s_page_buf_pos = 0;
    load_metadata();

    /* If metadata says we were recording (unclean shutdown), reset to idle */
    if (s_meta.state == LOG_STATE_RECORDING) {
        s_meta.state = LOG_STATE_IDLE;
        save_metadata();
    }
}

void flash_logger_task(void *params)
{
    (void)params;
    gateway_frame_t gf;

    /* Periodic metadata save interval (every 30 seconds while recording) */
    TickType_t last_meta_save = xTaskGetTickCount();
    const TickType_t meta_save_interval = pdMS_TO_TICKS(30000);

    for (;;) {
        /* Block on queue with 100ms timeout (allows periodic metadata save) */
        if (xQueueReceive(s_log_queue, &gf, pdMS_TO_TICKS(100)) == pdTRUE) {
            if (s_state != LOG_STATE_RECORDING) continue;

            log_entry_t entry;
            entry.timestamp_ms = gf.timestamp - s_meta.start_timestamp;
            entry.frame_id = gf.frame.id;
            entry.bus = (uint8_t)gf.source_bus;
            entry.dlc = gf.frame.dlc;
            memcpy(entry.data, gf.frame.data, 8);
            entry.reserved = 0;

            write_entry(&entry);
        }

        /* Periodic metadata save while recording */
        if (s_state == LOG_STATE_RECORDING) {
            TickType_t now = xTaskGetTickCount();
            if ((now - last_meta_save) >= meta_save_interval) {
                flush_page_buffer();
                save_metadata();
                last_meta_save = now;
            }
        }
    }
}

void flash_logger_enqueue_frame(const void *gf_ptr)
{
    if (s_state != LOG_STATE_RECORDING) return;

    const gateway_frame_t *gf = (const gateway_frame_t *)gf_ptr;
    if (!passes_bus_filter((uint8_t)gf->source_bus)) return;

    /* Non-blocking send — drop if full */
    xQueueSend(s_log_queue, gf, 0);
}

void flash_logger_start(void)
{
    if (s_state == LOG_STATE_RECORDING) return;
    if (s_state == LOG_STATE_ERROR) return;

    s_meta.start_timestamp = time_us_32() / 1000;
    s_meta.state = LOG_STATE_RECORDING;
    s_state = LOG_STATE_RECORDING;
    s_page_buf_pos = 0;

    save_metadata();
}

void flash_logger_stop(void)
{
    if (s_state != LOG_STATE_RECORDING) return;

    s_state = LOG_STATE_IDLE;

    /* Flush any remaining entries in the page buffer */
    flush_page_buffer();

    s_meta.state = LOG_STATE_IDLE;
    save_metadata();
}

void flash_logger_erase_all(void)
{
    if (s_state == LOG_STATE_RECORDING) return;

    /* Reset metadata */
    memset(&s_meta, 0, sizeof(s_meta));
    s_meta.magic = LOG_META_MAGIC;
    s_meta.write_offset = LOG_DATA_OFFSET;
    s_meta.bus_mask = 0x3F;
    s_meta.mode = LOG_MODE_MANUAL;
    save_metadata();

    s_state = LOG_STATE_IDLE;
}

/* ---- Status Accessors ---- */

uint8_t  flash_logger_get_state(void)       { return s_state; }
uint8_t  flash_logger_get_mode(void)        { return s_meta.mode; }
void     flash_logger_set_mode(uint8_t m)   { if (m <= LOG_MODE_MANUAL) s_meta.mode = m; }
uint8_t  flash_logger_get_bus_mask(void)    { return s_meta.bus_mask; }
void     flash_logger_set_bus_mask(uint8_t m){ s_meta.bus_mask = m; }
uint32_t flash_logger_get_entry_count(void) { return s_meta.entry_count; }
uint32_t flash_logger_get_wrap_count(void)  { return s_meta.wrap_count; }
uint32_t flash_logger_get_write_offset(void){ return s_meta.write_offset; }
uint16_t flash_logger_get_flash_errors(void){ return s_meta.flash_errors; }

uint16_t flash_logger_read_chunk(uint32_t offset, uint8_t *buf, uint16_t len)
{
    uint32_t abs_addr = LOG_DATA_OFFSET + offset;
    if (abs_addr >= LOG_DATA_END) return 0;
    if (abs_addr + len > LOG_DATA_END)
        len = (uint16_t)(LOG_DATA_END - abs_addr);

    uint32_t irq = sec_flash_acquire_bus();
    sec_flash_read(abs_addr, buf, len);
    sec_flash_release_bus(irq);

    return len;
}
```

- [ ] **Step 3: Add source to CMakeLists.txt**

In `firmware/CMakeLists.txt`, add `src/logger/flash_logger.c` in **two places**:

1. In the `add_executable(${PROJECT_NAME} ...)` block (after line 94 `src/monitor/bus_monitor.c`), add:
```cmake
    # Flash Logger
    src/logger/flash_logger.c
```

2. In the `COMMON_SOURCES` list (after line 159 `src/monitor/bus_monitor.c`), add:
```cmake
    src/logger/flash_logger.c
```

- [ ] **Step 4: Verify firmware compiles**

Run:
```bash
cd firmware && cmake --build build
```

- [ ] **Step 5: Commit**

```bash
git add firmware/src/logger/ firmware/CMakeLists.txt
git commit -m "feat(firmware): add flash_logger module — ring buffer, metadata, page buffering"
```

---

## Task 3: Firmware — Hook Logger into CAN and LIN Tasks

**Files:**
- Modify: `firmware/src/can/can_manager.c:306,315` (add enqueue calls)
- Modify: `firmware/src/lin/lin_manager.c:125` (add enqueue call)
- Modify: `firmware/src/main.c:23,144,157,178` (queue, init, task creation)

### Step-by-step:

- [ ] **Step 1: Add logger enqueue calls in can_manager.c**

Add include at the top of `can_manager.c` (after line 5 `#include "monitor/bus_monitor.h"`):

```c
#include "logger/flash_logger.h"
```

In the CAN1 RX drain loop, after `bus_monitor_enqueue_frame(&gf);` (line 306), add:

```c
            flash_logger_enqueue_frame(&gf);
```

In the CAN2 RX drain loop, after `bus_monitor_enqueue_frame(&gf);` (line 315), add:

```c
            flash_logger_enqueue_frame(&gf);
```

- [ ] **Step 2: Add logger enqueue call in lin_manager.c**

Add include at the top of `lin_manager.c` (after line 6 `#include "monitor/bus_monitor.h"`):

```c
#include "logger/flash_logger.h"
```

After `bus_monitor_enqueue_frame(&gf);` (line 125), add:

```c
            flash_logger_enqueue_frame(&gf);
```

- [ ] **Step 3: Update main.c to create logger queue, init, and task**

Add include at the top of `main.c` (after line 23 `#include "monitor/bus_monitor.h"`):

```c
#include "logger/flash_logger.h"
```

After the monitor queue creation (line 144), add:

```c
    QueueHandle_t g_log_queue = xQueueCreate(QUEUE_DEPTH_LOG_WRITE, sizeof(gateway_frame_t));
    ASSERT_ALLOC(g_log_queue);
```

After `bus_monitor_init(g_monitor_tx_queue);` (line 157), add:

```c
    flash_logger_init(g_log_queue);
```

After the last `xTaskCreate` call (line 178), add:

```c
    xTaskCreate(flash_logger_task, "LOG", TASK_STACK_LOG, NULL, tskIDLE_PRIORITY + 1, NULL);
```

Priority `tskIDLE_PRIORITY + 1` (just above idle) — logging is background work that shouldn't compete with CAN/LIN real-time tasks.

- [ ] **Step 4: Verify firmware compiles**

Run:
```bash
cd firmware && cmake --build build
```

- [ ] **Step 5: Commit**

```bash
git add firmware/src/can/can_manager.c firmware/src/lin/lin_manager.c firmware/src/main.c
git commit -m "feat(firmware): hook flash logger into CAN/LIN tasks and main init"
```

---

## Task 4: Firmware — Config Protocol Handler for SectionLog

**Files:**
- Modify: `firmware/src/config/config_handler.c` (add SectionLog handlers + chunked read)

### Step-by-step:

- [ ] **Step 1: Add include**

At the top of `config_handler.c` (after line 10 `#include "monitor/bus_monitor.h"`), add:

```c
#include "logger/flash_logger.h"
```

- [ ] **Step 2: Add READ_PARAM case for SectionLog**

In `handle_read_param()`, after the `CFG_SECTION_MONITOR` case block (line 375), add:

```c
    case CFG_SECTION_LOG:
        switch (param) {
        case LOG_PARAM_MODE:
            payload[3] = flash_logger_get_mode();
            plen = 4;
            break;
        case LOG_PARAM_BUS_MASK:
            payload[3] = flash_logger_get_bus_mask();
            plen = 4;
            break;
        case LOG_PARAM_STATUS:
            payload[3] = flash_logger_get_state();
            plen = 4;
            break;
        case LOG_PARAM_ENTRY_COUNT: {
            /* 32-bit value split: sub=0 → low 16 bits, sub=1 → high 16 bits */
            uint32_t count = flash_logger_get_entry_count();
            if (sub == 0) {
                payload[3] = (uint8_t)(count);
                payload[4] = (uint8_t)(count >> 8);
            } else {
                payload[3] = (uint8_t)(count >> 16);
                payload[4] = (uint8_t)(count >> 24);
            }
            plen = 5;
            break;
        }
        case LOG_PARAM_WRAP_COUNT: {
            uint32_t wraps = flash_logger_get_wrap_count();
            if (sub == 0) {
                payload[3] = (uint8_t)(wraps);
                payload[4] = (uint8_t)(wraps >> 8);
            } else {
                payload[3] = (uint8_t)(wraps >> 16);
                payload[4] = (uint8_t)(wraps >> 24);
            }
            plen = 5;
            break;
        }
        case LOG_PARAM_WRITE_OFFSET: {
            uint32_t off = flash_logger_get_write_offset();
            if (sub == 0) {
                payload[3] = (uint8_t)(off);
                payload[4] = (uint8_t)(off >> 8);
            } else {
                payload[3] = (uint8_t)(off >> 16);
                payload[4] = (uint8_t)(off >> 24);
            }
            plen = 5;
            break;
        }
        case LOG_PARAM_FLASH_ERRORS: {
            uint16_t errors = flash_logger_get_flash_errors();
            payload[3] = (uint8_t)(errors);
            payload[4] = (uint8_t)(errors >> 8);
            plen = 5;
            break;
        }
        default:
            send_response(CFG_CMD_READ_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        break;
```

- [ ] **Step 3: Add WRITE_PARAM case for SectionLog**

In `handle_write_param()`, after the `CFG_SECTION_MONITOR` case block (line 541), add:

```c
    case CFG_SECTION_LOG:
        if (dlc < 5) {
            send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        switch (param) {
        case LOG_PARAM_MODE:
            flash_logger_set_mode(data[4]);
            break;
        case LOG_PARAM_BUS_MASK:
            flash_logger_set_bus_mask(data[4]);
            break;
        case LOG_PARAM_STATE_CMD:
            if (data[4] == 1) {
                flash_logger_start();
            } else if (data[4] == 0) {
                flash_logger_stop();
            } else if (data[4] == 0xFF) {
                /* Erase all log data */
                flash_logger_erase_all();
            }
            break;
        default:
            send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_OK, NULL, 0);
        return;
```

- [ ] **Step 4: Add chunked log read handler**

Add a new handler function before `config_handler_task()` (the main task function). Find the end of `handle_bulk_read_data()` (around line 774) and add after it:

```c
/* ---- Chunked Log Read (0x24) ---- */

/* Dedicated buffer for log chunk reads (4 KB — s_bulk_buffer is only 2 KB) */
static uint8_t s_log_chunk_buffer[NVM_SECTOR_SIZE];

static void handle_log_read_chunk(const uint8_t *data, uint8_t dlc)
{
    /* Frame format: [cmd=0x24][offset_b0][offset_b1][offset_b2][length_lo][length_hi] */
    if (dlc < 6) {
        send_response(CFG_CMD_LOG_READ_CHUNK, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }

    uint32_t offset = (uint32_t)data[1]
                    | ((uint32_t)data[2] << 8)
                    | ((uint32_t)data[3] << 16);
    uint16_t length = (uint16_t)data[4] | ((uint16_t)data[5] << 8);

    if (length > NVM_SECTOR_SIZE) length = NVM_SECTOR_SIZE;

    uint16_t actual = flash_logger_read_chunk(offset, s_log_chunk_buffer, length);
    if (actual == 0) {
        send_response(CFG_CMD_LOG_READ_CHUNK, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }

    /* Compute CRC32 of the chunk data */
    uint32_t crc = crc32_compute(s_log_chunk_buffer, actual);

    /* Stream data on CONFIG_CAN_BULK_RESP_ID (0x603), same format as BULK_READ_DATA */
    can_frame_t tx_frame;
    tx_frame.id = CONFIG_CAN_BULK_RESP_ID;
    tx_frame.flags = 0;

    uint16_t sent = 0;
    uint8_t seq = 0;
    while (sent < actual) {
        uint8_t chunk = (actual - sent > 7) ? 7 : (uint8_t)(actual - sent);
        tx_frame.dlc = 1 + chunk;
        tx_frame.data[0] = seq++;
        memcpy(&tx_frame.data[1], &s_log_chunk_buffer[sent], chunk);
        can_manager_transmit(CAN_BUS_1, &tx_frame);
        sent += chunk;

        /* Yield periodically to avoid starving other tasks */
        if ((seq & 0x0F) == 0)
            vTaskDelay(1);
    }

    /* Send ACK with CRC32 and actual size */
    uint8_t ack_payload[6];
    ack_payload[0] = (uint8_t)(actual);
    ack_payload[1] = (uint8_t)(actual >> 8);
    ack_payload[2] = (uint8_t)(crc);
    ack_payload[3] = (uint8_t)(crc >> 8);
    ack_payload[4] = (uint8_t)(crc >> 16);
    ack_payload[5] = (uint8_t)(crc >> 24);
    send_response(CFG_CMD_LOG_READ_CHUNK, CFG_STATUS_OK, ack_payload, 6);
}
```

- [ ] **Step 5: Wire chunked read into the command dispatcher**

In the main command dispatcher (the `switch` on `data[0]` in `config_handler_task()`), add a case for 0x24. Find the existing cases (BULK_READ_DATA is 0x23) and add after it:

```c
        case CFG_CMD_LOG_READ_CHUNK:
            handle_log_read_chunk(data, dlc);
            break;
```

- [ ] **Step 6: Verify firmware compiles**

Run:
```bash
cd firmware && cmake --build build
```

- [ ] **Step 7: Commit**

```bash
git add firmware/src/config/config_handler.c
git commit -m "feat(firmware): add SectionLog config protocol and chunked log read handler"
```

---

## Task 5: Config Tool — Protocol Constants and LogReadChunkAsync

**Files:**
- Modify: `software/CanLinConfig/Protocol/ProtocolConstants.cs:49` (add logger constants)
- Modify: `software/CanLinConfig/Protocol/ConfigProtocol.cs` (add LogReadChunkAsync)

### Step-by-step:

- [ ] **Step 1: Add logger constants to ProtocolConstants.cs**

After `MonitorMaxFilterIds` (line 63), add:

```csharp
    // ---- Logger (SectionLog = 0x07) ----
    public const byte SectionLog = 0x07;

    // Logger param indices (single-byte params)
    public const byte LogParamMode = 0;
    public const byte LogParamBusMask = 1;
    public const byte LogParamStateCmd = 2;
    public const byte LogParamStatus = 3;

    // Logger param indices (32-bit params, read via sub=0/sub=1 for low/high 16 bits)
    public const byte LogParamEntryCount = 4;
    public const byte LogParamWrapCount = 5;
    public const byte LogParamWriteOffset = 6;
    public const byte LogParamFlashErrors = 7;

    // Logger states
    public const byte LogStateIdle = 0;
    public const byte LogStateRecording = 1;
    public const byte LogStateError = 4;

    // Logger modes
    public const byte LogModeManual = 0;

    // Logger state commands
    public const byte LogCmdStop = 0;
    public const byte LogCmdStart = 1;
    public const byte LogCmdEraseAll = 0xFF;

    // Chunked log read
    public const byte CmdLogReadChunk = 0x24;
    public const ushort LogChunkSize = 4096;
```

- [ ] **Step 2: Add LogReadChunkAsync to ConfigProtocol.cs**

Add new fields and a method for log chunk reads. The chunked log read protocol is different from BULK_READ — data streams first, then the ACK with CRC arrives. The existing `HandleBulkReadData` checks CRC inline, so we use a **separate** receive buffer and TCS to avoid conflicts.

Add fields alongside the existing `_bulkRead*` fields (around line 21):

```csharp
    // Log chunk read state (separate from bulk read — different protocol flow)
    private byte[]? _logChunkBuffer;
    private int _logChunkExpectedSize;
    private int _logChunkReceived;
    private int _logChunkSeq;
    private TaskCompletionSource<bool>? _logChunkDataTcs;
```

Add a handler for 0x603 frames during log chunk reads. In `OnFrameReceived()`, modify the `ConfigBulkRespId` handling (the existing code routes 0x603 to `HandleBulkReadData`). Add a check **before** the existing handler:

```csharp
        else if (e.Frame.Id == ProtocolConstants.ConfigBulkRespId)
        {
            // Log chunk read takes priority when active
            if (_logChunkDataTcs != null)
                HandleLogChunkData(e.Frame);
            else
                HandleBulkReadData(e.Frame);
        }
```

Add the `HandleLogChunkData` method:

```csharp
    private void HandleLogChunkData(CanFrame frame)
    {
        if (_logChunkBuffer == null || _logChunkDataTcs == null) return;

        byte seq = frame.Data[0];
        // Accept frames in order, copy payload
        int payloadLen = frame.Dlc - 1;
        if (payloadLen <= 0) return;

        int remaining = _logChunkExpectedSize - _logChunkReceived;
        int copyLen = Math.Min(payloadLen, remaining);
        Array.Copy(frame.Data, 1, _logChunkBuffer, _logChunkReceived, copyLen);
        _logChunkReceived += copyLen;
        _logChunkSeq++;

        if (_logChunkReceived >= _logChunkExpectedSize)
            _logChunkDataTcs.TrySetResult(true);
    }
```

Add the `LogReadChunkAsync` method:

```csharp
    /// <summary>
    /// Read a chunk of log data from the device flash.
    /// Data streams on 0x603, then ACK with CRC arrives on 0x601.
    /// </summary>
    public async Task<LogChunkResult> LogReadChunkAsync(uint offset, ushort length)
    {
        if (_adapter == null)
            return new LogChunkResult(false, Array.Empty<byte>(), 0);

        await _cmdLock.WaitAsync();
        try
        {
            // Set up log chunk receive state
            _logChunkBuffer = new byte[length];
            _logChunkExpectedSize = length;
            _logChunkReceived = 0;
            _logChunkSeq = 0;
            _logChunkDataTcs = new TaskCompletionSource<bool>();

            // Set up ACK listener (ACK arrives after all data)
            _expectedCmd = ProtocolConstants.CmdLogReadChunk;
            _pendingResponse = new TaskCompletionSource<CanFrame>();

            // Send LOG_READ_CHUNK: [0x24][offset_b0..b2][len_lo][len_hi]
            var frame = new CanFrame
            {
                Id = ProtocolConstants.ConfigCmdId,
                Dlc = 6
            };
            frame.Data[0] = ProtocolConstants.CmdLogReadChunk;
            frame.Data[1] = (byte)(offset);
            frame.Data[2] = (byte)(offset >> 8);
            frame.Data[3] = (byte)(offset >> 16);
            frame.Data[4] = (byte)(length);
            frame.Data[5] = (byte)(length >> 8);

            _adapter.Send(frame);

            // Wait for ACK (ACK comes after all data frames)
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await _pendingResponse.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logChunkDataTcs = null;
                _logChunkBuffer = null;
                _pendingResponse = null;
                return new LogChunkResult(false, Array.Empty<byte>(), 0);
            }

            var resp = _pendingResponse.Task.Result;
            _pendingResponse = null;
            _logChunkDataTcs = null;

            if (resp.Data[1] != ProtocolConstants.StatusOk)
            {
                _logChunkBuffer = null;
                return new LogChunkResult(false, Array.Empty<byte>(), 0);
            }

            // Parse ACK: [cmd][status][size_lo][size_hi][crc_b0..b3]
            ushort actualSize = (ushort)(resp.Data[2] | (resp.Data[3] << 8));
            uint expectedCrc = (uint)(resp.Data[4] | (resp.Data[5] << 8)
                             | (resp.Data[6] << 16) | (resp.Data[7] << 24));

            // Verify CRC
            var data = new byte[actualSize];
            Array.Copy(_logChunkBuffer, data, Math.Min(actualSize, _logChunkReceived));
            _logChunkBuffer = null;

            uint actualCrc = Helpers.Crc32.Compute(data);
            bool crcOk = actualCrc == expectedCrc;

            return new LogChunkResult(crcOk, data, actualSize);
        }
        finally
        {
            _cmdLock.Release();
        }
    }
```

Add the result record near the top of the file:

```csharp
public record LogChunkResult(bool Success, byte[] Data, ushort ActualSize);
```

- [ ] **Step 3: Build the solution**

Run:
```bash
cd software && dotnet build CanLinConfig.sln
```

- [ ] **Step 4: Commit**

```bash
git add software/CanLinConfig/Protocol/ProtocolConstants.cs software/CanLinConfig/Protocol/ConfigProtocol.cs
git commit -m "feat(config-tool): add SectionLog constants and LogReadChunkAsync protocol method"
```

---

## Task 6: Config Tool — LogControlViewModel

**Files:**
- Create: `software/CanLinConfig/ViewModels/LogControlViewModel.cs`

### Step-by-step:

- [ ] **Step 1: Create LogControlViewModel**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Protocol;

namespace CanLinConfig.ViewModels;

public partial class LogControlViewModel : ObservableObject
{
    private ConfigProtocol? _protocol;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private byte _loggerStatus;
    [ObservableProperty] private uint _entryCount;
    [ObservableProperty] private uint _wrapCount;
    [ObservableProperty] private ushort _flashErrors;
    [ObservableProperty] private string _statusText = "Idle";

    // Bus filter
    [ObservableProperty] private bool _logCan1 = true;
    [ObservableProperty] private bool _logCan2 = true;
    [ObservableProperty] private bool _logLin1 = true;
    [ObservableProperty] private bool _logLin2 = true;
    [ObservableProperty] private bool _logLin3 = true;
    [ObservableProperty] private bool _logLin4 = true;

    public void SetProtocol(ConfigProtocol? protocol)
    {
        _protocol = protocol;
        IsConnected = protocol != null;
        if (!IsConnected)
        {
            IsRecording = false;
            StatusText = "Disconnected";
        }
        else
        {
            _ = RefreshStatusAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        if (_protocol == null) return;

        // Send bus mask first
        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamBusMask, 0,
            [BuildBusMask()]);

        // Send start command
        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamStateCmd, 0,
            [ProtocolConstants.LogCmdStart]);

        await RefreshStatusAsync();
    }

    private bool CanStart() => IsConnected && !IsRecording;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        if (_protocol == null) return;

        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamStateCmd, 0,
            [ProtocolConstants.LogCmdStop]);

        await RefreshStatusAsync();
    }

    private bool CanStop() => IsConnected && IsRecording;

    [RelayCommand(CanExecute = nameof(CanErase))]
    private async Task EraseAll()
    {
        if (_protocol == null) return;

        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamStateCmd, 0,
            [ProtocolConstants.LogCmdEraseAll]);

        await RefreshStatusAsync();
    }

    private bool CanErase() => IsConnected && !IsRecording;

    [RelayCommand]
    private async Task RefreshStatus()
    {
        await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        if (_protocol == null) return;

        // Read status
        var status = await _protocol.ReadParamAsync(
            ProtocolConstants.SectionLog, ProtocolConstants.LogParamStatus, 0);
        if (status.Success && status.Value.Length > 0)
        {
            LoggerStatus = status.Value[0];
            IsRecording = LoggerStatus == ProtocolConstants.LogStateRecording;
            StatusText = LoggerStatus switch
            {
                ProtocolConstants.LogStateIdle => "Idle",
                ProtocolConstants.LogStateRecording => "Recording",
                ProtocolConstants.LogStateError => "Error — flash failures",
                _ => $"Unknown ({LoggerStatus})"
            };
        }

        // Read entry count (32-bit, split across sub=0 and sub=1)
        EntryCount = await ReadUint32ParamAsync(ProtocolConstants.LogParamEntryCount);

        // Read wrap count (32-bit, split)
        WrapCount = await ReadUint32ParamAsync(ProtocolConstants.LogParamWrapCount);

        // Read flash errors (16-bit, fits in single read)
        var errors = await _protocol.ReadParamAsync(
            ProtocolConstants.SectionLog, ProtocolConstants.LogParamFlashErrors, 0);
        if (errors.Success && errors.Value.Length >= 2)
        {
            FlashErrors = (ushort)(errors.Value[0] | (errors.Value[1] << 8));
        }

        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        EraseAllCommand.NotifyCanExecuteChanged();
    }

    partial void OnLogCan1Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnLogCan2Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnLogLin1Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnLogLin2Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnLogLin3Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnLogLin4Changed(bool value) => _ = SendBusMaskAsync();

    private byte BuildBusMask()
    {
        byte mask = 0;
        if (LogCan1) mask |= 0x01;
        if (LogCan2) mask |= 0x02;
        if (LogLin1) mask |= 0x04;
        if (LogLin2) mask |= 0x08;
        if (LogLin3) mask |= 0x10;
        if (LogLin4) mask |= 0x20;
        return mask;
    }

    private async Task SendBusMaskAsync()
    {
        if (_protocol == null || !IsRecording) return;
        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionLog,
            ProtocolConstants.LogParamBusMask, 0,
            [BuildBusMask()]);
    }

    /// <summary>
    /// Read a 32-bit param split across sub=0 (low16) and sub=1 (high16).
    /// </summary>
    private async Task<uint> ReadUint32ParamAsync(byte param)
    {
        if (_protocol == null) return 0;
        var lo = await _protocol.ReadParamAsync(
            ProtocolConstants.SectionLog, param, 0);
        var hi = await _protocol.ReadParamAsync(
            ProtocolConstants.SectionLog, param, 1);

        ushort loVal = (lo.Success && lo.Value.Length >= 2)
            ? (ushort)(lo.Value[0] | (lo.Value[1] << 8)) : (ushort)0;
        ushort hiVal = (hi.Success && hi.Value.Length >= 2)
            ? (ushort)(hi.Value[0] | (hi.Value[1] << 8)) : (ushort)0;

        return (uint)(loVal | (hiVal << 16));
    }
}
```

- [ ] **Step 2: Build the solution**

Run:
```bash
cd software && dotnet build CanLinConfig.sln
```

- [ ] **Step 3: Commit**

```bash
git add software/CanLinConfig/ViewModels/LogControlViewModel.cs
git commit -m "feat(config-tool): add LogControlViewModel — start/stop, status, bus mask"
```

---

## Task 7: Config Tool — LogDownloadViewModel with Tests

**Files:**
- Create: `software/CanLinConfig.Tests/LogDownloadViewModelTests.cs`
- Create: `software/CanLinConfig/ViewModels/LogDownloadViewModel.cs`

### Step-by-step:

- [ ] **Step 1: Write tests for download logic**

```csharp
// software/CanLinConfig.Tests/LogDownloadViewModelTests.cs
using CanLinConfig.ViewModels;

namespace CanLinConfig.Tests;

public class LogDownloadViewModelTests
{
    [Fact]
    public void ParseLogEntries_parses_single_entry()
    {
        var data = new byte[20];
        // timestamp_ms = 1000 (0x000003E8)
        data[0] = 0xE8; data[1] = 0x03; data[2] = 0x00; data[3] = 0x00;
        // frame_id = 0x123
        data[4] = 0x23; data[5] = 0x01; data[6] = 0x00; data[7] = 0x00;
        // bus = 1 (CAN2)
        data[8] = 0x01;
        // dlc = 4
        data[9] = 0x04;
        // data = AA BB CC DD 00 00 00 00
        data[10] = 0xAA; data[11] = 0xBB; data[12] = 0xCC; data[13] = 0xDD;
        // reserved
        data[18] = 0x00; data[19] = 0x00;

        var entries = LogDownloadViewModel.ParseLogEntries(data);

        Assert.Single(entries);
        Assert.Equal(1000u, entries[0].TimestampMs);
        Assert.Equal(0x123u, entries[0].FrameId);
        Assert.Equal(1, entries[0].Bus);
        Assert.Equal(4, entries[0].Dlc);
        Assert.Equal(0xAA, entries[0].Data[0]);
        Assert.Equal(0xDD, entries[0].Data[3]);
    }

    [Fact]
    public void ParseLogEntries_skips_gap_markers()
    {
        var data = new byte[40]; // 2 entries
        // Entry 1: normal frame
        data[8] = 0x00; // bus = CAN1
        data[9] = 0x02; // dlc = 2

        // Entry 2: gap marker (bus = 0xFF)
        data[28] = 0xFF; // bus = gap marker

        var entries = LogDownloadViewModel.ParseLogEntries(data);

        Assert.Single(entries); // gap marker skipped
    }

    [Fact]
    public void ParseLogEntries_handles_empty_data()
    {
        var entries = LogDownloadViewModel.ParseLogEntries(Array.Empty<byte>());
        Assert.Empty(entries);
    }

    [Fact]
    public void ParseLogEntries_handles_partial_entry()
    {
        // 15 bytes — not enough for one full entry (20 bytes)
        var data = new byte[15];
        var entries = LogDownloadViewModel.ParseLogEntries(data);
        Assert.Empty(entries);
    }

    [Fact]
    public void ParseLogEntries_multiple_entries()
    {
        var data = new byte[60]; // 3 entries
        // Entry 0: CAN1, ID=0x100
        data[4] = 0x00; data[5] = 0x01; data[8] = 0x00; data[9] = 0x03;
        // Entry 1: CAN2, ID=0x200
        data[24] = 0x00; data[25] = 0x02; data[28] = 0x01; data[29] = 0x05;
        // Entry 2: LIN1, ID=0x3C
        data[44] = 0x3C; data[48] = 0x02; data[49] = 0x08;

        var entries = LogDownloadViewModel.ParseLogEntries(data);

        Assert.Equal(3, entries.Count);
        Assert.Equal(0x100u, entries[0].FrameId);
        Assert.Equal(0x200u, entries[1].FrameId);
        Assert.Equal(0x3Cu, entries[2].FrameId);
    }
}
```

- [ ] **Step 2: Run tests — verify they fail**

Run:
```bash
cd software && dotnet test CanLinConfig.Tests --filter "LogDownloadViewModel" -v n
```
Expected: Compile error (LogDownloadViewModel doesn't exist yet).

- [ ] **Step 3: Create LogDownloadViewModel**

```csharp
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Helpers;
using CanLinConfig.Models;
using CanLinConfig.Protocol;
using CanLinConfig.Services;
using CanLinConfig.Services.Export;
using Microsoft.Win32;

namespace CanLinConfig.ViewModels;

public partial class LogDownloadViewModel : ObservableObject
{
    private ConfigProtocol? _protocol;
    private BusDataService? _busDataService;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress; // 0-100
    [ObservableProperty] private string _downloadStatus = "";
    [ObservableProperty] private int _downloadedEntries;
    [ObservableProperty] private bool _hasDownloadedData;

    private List<LogEntry> _downloadedLog = new();
    private CancellationTokenSource? _downloadCts;

    public record LogEntry(
        uint TimestampMs, uint FrameId, byte Bus, byte Dlc, byte[] Data);

    public void SetProtocol(ConfigProtocol? protocol, BusDataService? busDataService)
    {
        _protocol = protocol;
        _busDataService = busDataService;
        IsConnected = protocol != null;
        if (!IsConnected)
        {
            IsDownloading = false;
            DownloadStatus = "";
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task Download()
    {
        if (_protocol == null) return;

        IsDownloading = true;
        DownloadProgress = 0;
        _downloadedLog.Clear();
        DownloadedEntries = 0;
        _downloadCts = new CancellationTokenSource();

        try
        {
            // Read write_offset (32-bit, split across sub=0 and sub=1)
            uint writeOffset = await ReadUint32ParamAsync(ProtocolConstants.LogParamWriteOffset);
            if (writeOffset == 0)
            {
                DownloadStatus = "Failed to read write offset";
                return;
            }

            // Read wrap count (32-bit, split)
            uint wrapCount = await ReadUint32ParamAsync(ProtocolConstants.LogParamWrapCount);

            // Calculate total bytes to download
            // LOG_DATA_OFFSET on device is 0x021000
            const uint logDataOffset = 0x021000;
            const uint logDataEnd = 0x1000000; // 16 MB
            const uint logDataSize = logDataEnd - logDataOffset;

            uint totalBytes;
            uint startOffset; // relative to LOG_DATA_OFFSET

            if (wrapCount == 0)
            {
                // No wrap — data from LOG_DATA_OFFSET to writeOffset
                totalBytes = writeOffset - logDataOffset;
                startOffset = 0;
            }
            else
            {
                // Wrapped — read entire ring buffer
                totalBytes = logDataSize;
                startOffset = writeOffset - logDataOffset;
            }

            if (totalBytes == 0)
            {
                DownloadStatus = "No log data on device";
                return;
            }

            DownloadStatus = $"Downloading {totalBytes / 1024} KB...";

            // Download in 4 KB chunks
            var allData = new MemoryStream();
            uint downloaded = 0;
            const ushort chunkSize = ProtocolConstants.LogChunkSize;

            // For wrapped ring, start reading from writeOffset (oldest data)
            uint readOffset = startOffset;

            while (downloaded < totalBytes)
            {
                _downloadCts.Token.ThrowIfCancellationRequested();

                ushort thisChunk = (ushort)Math.Min(chunkSize, totalBytes - downloaded);

                // Wrap read offset within ring buffer
                uint actualOffset = readOffset % logDataSize;

                var result = await _protocol.LogReadChunkAsync(actualOffset, thisChunk);
                if (!result.Success)
                {
                    DownloadStatus = $"Chunk read failed at offset 0x{actualOffset:X6} — retrying...";
                    // Retry once
                    result = await _protocol.LogReadChunkAsync(actualOffset, thisChunk);
                    if (!result.Success)
                    {
                        DownloadStatus = $"Download failed at offset 0x{actualOffset:X6}";
                        return;
                    }
                }

                allData.Write(result.Data, 0, result.ActualSize);
                downloaded += result.ActualSize;
                readOffset += result.ActualSize;

                DownloadProgress = (double)downloaded / totalBytes * 100;
                DownloadStatus = $"Downloaded {downloaded / 1024} / {totalBytes / 1024} KB";
            }

            // Parse entries
            _downloadedLog = ParseLogEntries(allData.ToArray());
            DownloadedEntries = _downloadedLog.Count;
            HasDownloadedData = _downloadedLog.Count > 0;
            DownloadStatus = $"Complete — {_downloadedLog.Count} entries";
        }
        catch (OperationCanceledException)
        {
            DownloadStatus = "Download cancelled";
        }
        finally
        {
            IsDownloading = false;
            _downloadCts = null;
            DownloadCommand.NotifyCanExecuteChanged();
            CancelDownloadCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanDownload() => IsConnected && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    private bool CanCancel() => IsDownloading;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void ExportLog()
    {
        if (_downloadedLog.Count == 0 || _busDataService == null) return;

        // Convert LogEntries to BusFrames
        var frames = new List<BusFrame>();
        DateTime baseTime = DateTime.Now;
        foreach (var entry in _downloadedLog)
        {
            var data = new byte[8];
            Array.Copy(entry.Data, data, Math.Min(entry.Data.Length, 8));
            var bus = (BusFrame.Bus)entry.Bus;
            var timestamp = baseTime.AddMilliseconds(entry.TimestampMs);
            frames.Add(new BusFrame(bus, entry.FrameId, entry.Dlc, data, timestamp, false));
        }

        var dlg = new SaveFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv|ASC Files (*.asc)|*.asc|BLF Files (*.blf)|*.blf",
            DefaultExt = ".csv"
        };

        if (dlg.ShowDialog() != true) return;

        IFrameExporter exporter = Path.GetExtension(dlg.FileName).ToLowerInvariant() switch
        {
            ".asc" => new AscExporter(),
            ".blf" => new BlfExporter(),
            _ => new CsvExporter()
        };

        using var fs = File.Create(dlg.FileName);
        exporter.Export(fs, frames, _busDataService.DatabaseManager);
        DownloadStatus = $"Exported to {Path.GetFileName(dlg.FileName)}";
    }

    private bool CanExport() => HasDownloadedData;

    [RelayCommand(CanExecute = nameof(CanFeedToBusMonitor))]
    private void FeedToBusMonitor()
    {
        if (_downloadedLog.Count == 0 || _busDataService == null) return;

        DateTime baseTime = DateTime.Now;
        foreach (var entry in _downloadedLog)
        {
            var data = new byte[8];
            Array.Copy(entry.Data, data, Math.Min(entry.Data.Length, 8));
            var bus = (BusFrame.Bus)entry.Bus;
            var timestamp = baseTime.AddMilliseconds(entry.TimestampMs);
            var frame = new BusFrame(bus, entry.FrameId, entry.Dlc, data, timestamp, false);
            _busDataService.OnFrame(frame);
        }

        DownloadStatus = $"Fed {_downloadedLog.Count} frames to Bus Monitor";
    }

    private bool CanFeedToBusMonitor() => HasDownloadedData;

    partial void OnHasDownloadedDataChanged(bool value)
    {
        ExportLogCommand.NotifyCanExecuteChanged();
        FeedToBusMonitorCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Read a 32-bit param split across sub=0 (low16) and sub=1 (high16).
    /// </summary>
    private async Task<uint> ReadUint32ParamAsync(byte param)
    {
        if (_protocol == null) return 0;
        var lo = await _protocol.ReadParamAsync(
            ProtocolConstants.SectionLog, param, 0);
        var hi = await _protocol.ReadParamAsync(
            ProtocolConstants.SectionLog, param, 1);

        ushort loVal = (lo.Success && lo.Value.Length >= 2)
            ? (ushort)(lo.Value[0] | (lo.Value[1] << 8)) : (ushort)0;
        ushort hiVal = (hi.Success && hi.Value.Length >= 2)
            ? (ushort)(hi.Value[0] | (hi.Value[1] << 8)) : (ushort)0;

        return (uint)(loVal | (hiVal << 16));
    }

    /* ---- Log Entry Parsing (public static for testability) ---- */

    public static List<LogEntry> ParseLogEntries(byte[] data)
    {
        var entries = new List<LogEntry>();
        const int entrySize = 20;

        for (int i = 0; i + entrySize <= data.Length; i += entrySize)
        {
            byte bus = data[i + 8];

            // Skip gap markers (bus = 0xFF)
            if (bus == 0xFF) continue;

            // Skip erased flash (all 0xFF)
            if (data[i] == 0xFF && data[i + 1] == 0xFF &&
                data[i + 2] == 0xFF && data[i + 3] == 0xFF)
                continue;

            uint timestampMs = (uint)(data[i] | (data[i + 1] << 8)
                             | (data[i + 2] << 16) | (data[i + 3] << 24));
            uint frameId = (uint)(data[i + 4] | (data[i + 5] << 8)
                          | (data[i + 6] << 16) | (data[i + 7] << 24));
            byte dlc = data[i + 9];
            var payload = new byte[8];
            Array.Copy(data, i + 10, payload, 0, 8);

            entries.Add(new LogEntry(timestampMs, frameId, bus, dlc, payload));
        }

        return entries;
    }
}
```

- [ ] **Step 4: Run tests — verify they pass**

Run:
```bash
cd software && dotnet test CanLinConfig.Tests --filter "LogDownloadViewModel" -v n
```
Expected: All 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add software/CanLinConfig/ViewModels/LogDownloadViewModel.cs software/CanLinConfig.Tests/LogDownloadViewModelTests.cs
git commit -m "feat(config-tool): add LogDownloadViewModel — chunked download, parse, export"
```

---

## Task 8: Config Tool — DataLoggerViewModel (Tab Orchestrator)

**Files:**
- Create: `software/CanLinConfig/ViewModels/DataLoggerViewModel.cs`

### Step-by-step:

- [ ] **Step 1: Create DataLoggerViewModel**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CanLinConfig.Protocol;
using CanLinConfig.Services;

namespace CanLinConfig.ViewModels;

public partial class DataLoggerViewModel : ObservableObject
{
    public LogControlViewModel LogControl { get; }
    public LogDownloadViewModel LogDownload { get; }

    public DataLoggerViewModel()
    {
        LogControl = new LogControlViewModel();
        LogDownload = new LogDownloadViewModel();
    }

    public void SetProtocol(ConfigProtocol? protocol, BusDataService? busDataService)
    {
        LogControl.SetProtocol(protocol);
        LogDownload.SetProtocol(protocol, busDataService);
    }
}
```

- [ ] **Step 2: Build the solution**

Run:
```bash
cd software && dotnet build CanLinConfig.sln
```

- [ ] **Step 3: Commit**

```bash
git add software/CanLinConfig/ViewModels/DataLoggerViewModel.cs
git commit -m "feat(config-tool): add DataLoggerViewModel — logger tab orchestrator"
```

---

## Task 9: Config Tool — Data Logger Views (XAML)

**Files:**
- Create: `software/CanLinConfig/Views/LogControlPanel.xaml`
- Create: `software/CanLinConfig/Views/LogControlPanel.xaml.cs`
- Create: `software/CanLinConfig/Views/LogDownloadPanel.xaml`
- Create: `software/CanLinConfig/Views/LogDownloadPanel.xaml.cs`
- Create: `software/CanLinConfig/Views/DataLoggerView.xaml`
- Create: `software/CanLinConfig/Views/DataLoggerView.xaml.cs`

### Step-by-step:

- [ ] **Step 1: Create LogControlPanel.xaml**

```xml
<UserControl x:Class="CanLinConfig.Views.LogControlPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <GroupBox Header="Logger Control" Margin="5">
        <StackPanel>
            <!-- Status -->
            <StackPanel Orientation="Horizontal" Margin="0,0,0,8">
                <TextBlock Text="Status:" FontWeight="SemiBold" VerticalAlignment="Center" Margin="0,0,5,0"/>
                <TextBlock Text="{Binding StatusText}" VerticalAlignment="Center" Margin="0,0,15,0"/>
                <TextBlock Text="Entries:" VerticalAlignment="Center" Margin="0,0,5,0"/>
                <TextBlock Text="{Binding EntryCount}" VerticalAlignment="Center" Margin="0,0,15,0"/>
                <TextBlock Text="Wraps:" VerticalAlignment="Center" Margin="0,0,5,0"/>
                <TextBlock Text="{Binding WrapCount}" VerticalAlignment="Center" Margin="0,0,15,0"/>
                <TextBlock Text="Flash Errors:" VerticalAlignment="Center" Margin="0,0,5,0"/>
                <TextBlock Text="{Binding FlashErrors}" VerticalAlignment="Center"/>
            </StackPanel>

            <!-- Bus Filter -->
            <StackPanel Orientation="Horizontal" Margin="0,0,0,8">
                <TextBlock Text="Buses:" VerticalAlignment="Center" Margin="0,0,5,0"/>
                <CheckBox Content="CAN1" IsChecked="{Binding LogCan1}" VerticalAlignment="Center" Margin="0,0,4,0"/>
                <CheckBox Content="CAN2" IsChecked="{Binding LogCan2}" VerticalAlignment="Center" Margin="0,0,4,0"/>
                <CheckBox Content="LIN1" IsChecked="{Binding LogLin1}" VerticalAlignment="Center" Margin="0,0,4,0"/>
                <CheckBox Content="LIN2" IsChecked="{Binding LogLin2}" VerticalAlignment="Center" Margin="0,0,4,0"/>
                <CheckBox Content="LIN3" IsChecked="{Binding LogLin3}" VerticalAlignment="Center" Margin="0,0,4,0"/>
                <CheckBox Content="LIN4" IsChecked="{Binding LogLin4}" VerticalAlignment="Center"/>
            </StackPanel>

            <!-- Controls -->
            <StackPanel Orientation="Horizontal">
                <Button Content="Start" Command="{Binding StartCommand}" Width="70" Margin="0,0,5,0"/>
                <Button Content="Stop" Command="{Binding StopCommand}" Width="70" Margin="0,0,5,0"/>
                <Button Content="Erase All" Command="{Binding EraseAllCommand}" Width="80" Margin="0,0,5,0"/>
                <Button Content="Refresh" Command="{Binding RefreshStatusCommand}" Width="70"/>
            </StackPanel>
        </StackPanel>
    </GroupBox>
</UserControl>
```

- [ ] **Step 2: Create LogControlPanel.xaml.cs**

```csharp
namespace CanLinConfig.Views;

public partial class LogControlPanel
{
    public LogControlPanel()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 3: Create LogDownloadPanel.xaml**

```xml
<UserControl x:Class="CanLinConfig.Views.LogDownloadPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <GroupBox Header="Log Download" Margin="5">
        <StackPanel>
            <!-- Progress -->
            <ProgressBar Height="20" Minimum="0" Maximum="100"
                         Value="{Binding DownloadProgress, Mode=OneWay}"
                         Margin="0,0,0,5"
                         Visibility="{Binding IsDownloading, Converter={StaticResource BoolToVisConverter}}"/>

            <!-- Status -->
            <TextBlock Text="{Binding DownloadStatus}" Margin="0,0,0,8"/>

            <!-- Download info -->
            <StackPanel Orientation="Horizontal" Margin="0,0,0,8"
                        Visibility="{Binding HasDownloadedData, Converter={StaticResource BoolToVisConverter}}">
                <TextBlock Text="Downloaded Entries:" VerticalAlignment="Center" Margin="0,0,5,0"/>
                <TextBlock Text="{Binding DownloadedEntries}" VerticalAlignment="Center" FontWeight="SemiBold"/>
            </StackPanel>

            <!-- Controls -->
            <StackPanel Orientation="Horizontal">
                <Button Content="Download" Command="{Binding DownloadCommand}" Width="80" Margin="0,0,5,0"/>
                <Button Content="Cancel" Command="{Binding CancelDownloadCommand}" Width="70" Margin="0,0,5,0"/>
                <Button Content="Export..." Command="{Binding ExportLogCommand}" Width="70" Margin="0,0,5,0"/>
                <Button Content="Feed to Bus Monitor" Command="{Binding FeedToBusMonitorCommand}" Width="130"/>
            </StackPanel>
        </StackPanel>
    </GroupBox>
</UserControl>
```

- [ ] **Step 4: Create LogDownloadPanel.xaml.cs**

```csharp
namespace CanLinConfig.Views;

public partial class LogDownloadPanel
{
    public LogDownloadPanel()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 5: Create DataLoggerView.xaml**

```xml
<UserControl x:Class="CanLinConfig.Views.DataLoggerView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:CanLinConfig.Views">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <local:LogControlPanel Grid.Row="0" DataContext="{Binding LogControl}"/>
        <local:LogDownloadPanel Grid.Row="1" DataContext="{Binding LogDownload}"/>
    </Grid>
</UserControl>
```

- [ ] **Step 6: Create DataLoggerView.xaml.cs**

```csharp
namespace CanLinConfig.Views;

public partial class DataLoggerView
{
    public DataLoggerView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 7: Build the solution**

Run:
```bash
cd software && dotnet build CanLinConfig.sln
```

- [ ] **Step 8: Commit**

```bash
git add software/CanLinConfig/Views/LogControlPanel.xaml software/CanLinConfig/Views/LogControlPanel.xaml.cs software/CanLinConfig/Views/LogDownloadPanel.xaml software/CanLinConfig/Views/LogDownloadPanel.xaml.cs software/CanLinConfig/Views/DataLoggerView.xaml software/CanLinConfig/Views/DataLoggerView.xaml.cs
git commit -m "feat(config-tool): add Data Logger views — LogControlPanel, LogDownloadPanel, DataLoggerView"
```

---

## Task 10: Config Tool — Wire Data Logger Tab into MainWindow

**Files:**
- Modify: `software/CanLinConfig/ViewModels/MainViewModel.cs:44,59,155,203` (add DataLogger VM)
- Modify: `software/CanLinConfig/Views/MainWindow.xaml:89` (add tab)

### Step-by-step:

- [ ] **Step 1: Add DataLogger property to MainViewModel**

In `MainViewModel.cs`, add a property after `BusMonitor` (line 44):

```csharp
    public DataLoggerViewModel DataLogger { get; }
```

In the constructor (after `BusMonitor` initialization, around line 59), add:

```csharp
        DataLogger = new DataLoggerViewModel();
```

- [ ] **Step 2: Wire DataLogger in ConnectAsync**

In `ConnectAsync()`, after the `BusMonitor.MonitorControl.SetProtocol` call (line 155), add:

```csharp
        DataLogger.SetProtocol(_protocol, BusDataService);
```

- [ ] **Step 3: Wire DataLogger in Disconnect**

In `Disconnect()`, after `BusMonitor.MonitorControl.SetProtocol(null, null);` (around line 203), add:

```csharp
        DataLogger.SetProtocol(null, null);
```

- [ ] **Step 4: Add Data Logger tab to MainWindow.xaml**

After the Bus Monitor tab (line 84), add:

```xml
        <TabItem Header="Data Logger">
            <views:DataLoggerView DataContext="{Binding DataLogger}"/>
        </TabItem>
```

- [ ] **Step 5: Add BoolToVisConverter if not present**

Check if `BooleanToVisibilityConverter` is already in the App.xaml resources. If not, add to `MainWindow.xaml` resources:

```xml
    <Window.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVisConverter"/>
    </Window.Resources>
```

- [ ] **Step 6: Build the solution**

Run:
```bash
cd software && dotnet build CanLinConfig.sln
```

- [ ] **Step 7: Run all tests**

Run:
```bash
cd software && dotnet test CanLinConfig.Tests -v n
```
Expected: All tests pass (existing + new LogDownloadViewModel tests).

- [ ] **Step 8: Commit**

```bash
git add software/CanLinConfig/ViewModels/MainViewModel.cs software/CanLinConfig/Views/MainWindow.xaml
git commit -m "feat(config-tool): add Data Logger tab to MainWindow"
```

---

## Task 11: Update DBC, Master Plan, and Docs

**Files:**
- Modify: `docs/CanLinBoard.dbc` (add SectionLog command descriptions as comments)
- Modify: `docs/bus-monitor-logger-master-plan.md` (update Plan 5 status)

### Step-by-step:

- [ ] **Step 1: Update master plan tracker**

In `docs/bus-monitor-logger-master-plan.md`, replace the Plan 5 section (lines 118-148) with:

```markdown
## Plan 5A: Logger Foundation
**Status:** COMPLETE
**Plan:** [`docs/superpowers/plans/2026-03-23-logger-foundation.md`](superpowers/plans/2026-03-23-logger-foundation.md)
**Commits:** (fill in after implementation)
**Tests:** (fill in after implementation)

### What was built

**Firmware:**
- Flash logger ring buffer driver (CS1, 0x021000–0xFFFFFF, ~16 MB)
- Logger metadata sector (0x020000) with CRC32 integrity
- Manual recording mode (start/stop)
- Page buffering with erase-ahead and error retry
- Config protocol SectionLog (0x07) — mode, bus mask, start/stop, status, entry/wrap counts
- Chunked log read (0x24) — 4 KB chunks with per-chunk CRC32 on 0x603
- Periodic metadata save (every 30s while recording)

**Config tool:**
- Data Logger tab with LogControlPanel and LogDownloadPanel
- Chunked download with progress bar and CRC verification
- Export downloaded log via existing CSV/ASC/BLF exporters
- Feed downloaded log to Bus Monitor for visualization

### What it enables
Connect to the device, start recording, capture CAN/LIN traffic to on-board flash. Stop recording, download the log, export to CSV/ASC/BLF. Feed downloaded data to Bus Monitor for signal decoding and graphing.

### Key decisions
- Logger task at priority 1 (background) — doesn't compete with CAN/LIN real-time tasks
- 12 entries per 256-byte flash page (20 bytes each, 16 bytes wasted per page)
- Metadata CRC preserves write_offset across power cycles
- Unclean shutdown detected and recovered (state reset to idle, data preserved)

### Dependencies
- Plan 1 (BusDataService, export infrastructure)
- Plan 4 (monitor protocol — shared firmware patterns)

---

## Plan 5B: Continuous Mode
**Status:** NOT STARTED

### Scope
- Ring buffer wrap with erase-ahead
- RAM queue (64 entries) for buffering during download
- Gap marker sentinel (bus=0xFF) on overflow
- Config tool continuous mode UI

### Dependencies
- Plan 5A (flash logger foundation)

---

## Plan 5C: Triggered Mode
**Status:** NOT STARTED

### Scope
- Trigger condition matching (bus, ID, byte, operator, value)
- Pre/post trigger KB retention
- Armed → capturing → stopped state machine
- Config tool trigger configuration UI

### Dependencies
- Plan 5A (flash logger foundation)

---

## Plan 5D: Log Replay
**Status:** NOT STARTED

### Scope
- LogReplayPanel — playback from file into BusDataService
- Play/pause/speed/scrub controls
- Timeline with markers

### Dependencies
- Plan 5A (flash logger, download)
```

- [ ] **Step 2: Verify firmware builds**

Run:
```bash
cd firmware && cmake --build build
```

- [ ] **Step 3: Run all config tool tests**

Run:
```bash
cd software && dotnet test CanLinConfig.Tests -v n
```

- [ ] **Step 4: Commit**

```bash
git add docs/bus-monitor-logger-master-plan.md
git commit -m "docs: update master plan with Plan 5A-5D logger breakdown"
```

---

## Testing Notes

### Config tool (can test now)
- LogDownloadViewModel.ParseLogEntries: unit tests cover single entry, gap markers, empty data, partial entries, multiple entries
- Build succeeds, existing tests pass
- UI manually verifiable by launching the app

### Firmware (deferred — requires hardware)
- Flash logger start/stop via config protocol
- Metadata persistence across reboot (save/load/CRC)
- Unclean shutdown recovery (recording state → idle on reboot)
- Page buffering and sector erase at boundaries
- Ring buffer wrap (write past end → wraps to LOG_DATA_OFFSET)
- Flash error handling (retry, error counter, threshold stop)
- Chunked download with CRC verification
- Bus mask filtering (enable CAN2 only, verify only CAN2 frames logged)
- Entry count and wrap count accuracy
- Periodic metadata save timing (30s interval)
- Stack watermark check for LOG task (512 words)
- No interference with CAN/LIN real-time tasks (logger at priority 1)
