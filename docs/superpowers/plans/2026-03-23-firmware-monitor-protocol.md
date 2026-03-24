# Firmware Monitor Protocol — Implementation Plan

> **STATUS: COMPLETE AND TESTED (2026-03-24)** — On-target Phase 7: 16/16 tests pass (`tests/phase7/test_monitor_host.py`). All manual UI tests pass. MON-4, MON-7 N/A (need 2nd CAN adapter).

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stream CAN2/LIN1-4 traffic from the firmware to the config tool over CAN1, enabling multi-bus visibility in the Bus Monitor tab.

**Architecture:** Monitor logic runs inline in `can_task` and `lin_task` (no new FreeRTOS task). Each received frame is encoded as a header (0x604) + data (0x605) sequence-numbered pair and queued to a dedicated monitor TX queue. The CAN TX path drains monitor frames only when the main TX queue is empty. The config tool intercepts 0x604/0x605, pairs by sequence number, reconstructs BusFrames, and feeds BusDataService — all existing panels work automatically.

**Tech Stack:** C (firmware, FreeRTOS), C# .NET 8 (config tool, WPF, xUnit)

**Spec:** `docs/superpowers/specs/2026-03-23-bus-monitor-logger-design.md` § 2
**Master tracker:** `docs/bus-monitor-logger-master-plan.md` (Plan 4)
**Branch:** `feature/bus-monitor-foundation`

---

## File Map

### Firmware — New Files
| File | Purpose |
|------|---------|
| `firmware/src/monitor/bus_monitor.c` | Monitor state, frame encoding, queue management |
| `firmware/src/monitor/bus_monitor.h` | Public API: init, enable, enqueue, drain |

### Firmware — Modified Files
| File | Changes |
|------|---------|
| `firmware/include/board_config.h` | Add `MONITOR_HEADER_CAN_ID`, `MONITOR_DATA_CAN_ID`, `QUEUE_DEPTH_MONITOR_TX`, `MONITOR_MAX_FILTER_IDS` |
| `firmware/src/config/config_protocol.h` | Add `CFG_SECTION_MONITOR` |
| `firmware/src/config/config_handler.c` | Add `handle_read_param` / `handle_write_param` / `handle_bulk_start` cases for section 0x06 |
| `firmware/src/can/can_manager.c` | Call `bus_monitor_enqueue_frame()` after RX drain; drain monitor TX queue after main TX queue |
| `firmware/src/lin/lin_manager.c` | Call `bus_monitor_enqueue_frame()` after LIN RX in `process_channel_interrupt()` |
| `firmware/src/main.c` | Create `g_monitor_tx_queue`, pass to `bus_monitor_init()` and `can_manager_init()` |
| `firmware/CMakeLists.txt` | Add `src/monitor/bus_monitor.c` to source list |

### Config Tool — New Files
| File | Purpose |
|------|---------|
| `software/CanLinConfig/Services/MonitorFrameDecoder.cs` | Pairs 0x604/0x605 frames by sequence number, reconstructs BusFrame |
| `software/CanLinConfig/ViewModels/MonitorControlViewModel.cs` | Enable/disable, bus filter checkboxes, filter mode, gap counter |
| `software/CanLinConfig/Views/MonitorControlPanel.xaml` | Compact control panel UI |
| `software/CanLinConfig/Views/MonitorControlPanel.xaml.cs` | Code-behind (empty) |
| `software/CanLinConfig.Tests/MonitorFrameDecoderTests.cs` | Unit tests for frame decode logic |

### Config Tool — Modified Files
| File | Changes |
|------|---------|
| `software/CanLinConfig/Protocol/ProtocolConstants.cs` | Add `SectionMonitor`, monitor param constants |
| `software/CanLinConfig/Protocol/ConfigProtocol.cs` | Intercept 0x604/0x605 in `OnFrameReceived`, expose `MonitorFrameReceived` event |
| `software/CanLinConfig/ViewModels/BusMonitorViewModel.cs` | Add `MonitorControl` sub-VM, subscribe to monitor frames |
| `software/CanLinConfig/Views/BusMonitorView.xaml` | Add MonitorControlPanel row |
| `software/CanLinConfig/ViewModels/MainViewModel.cs` | Wire monitor frame events through BusDataService |

---

## Task 1: Firmware — Monitor Module (bus_monitor.c/h)

**Files:**
- Create: `firmware/src/monitor/bus_monitor.h`
- Create: `firmware/src/monitor/bus_monitor.c`
- Modify: `firmware/include/board_config.h:118-125` (add constants)
- Modify: `firmware/src/config/config_protocol.h:24` (add section)
- Modify: `firmware/CMakeLists.txt` (add source file)

### Step-by-step:

- [x] **Step 1: Add constants to board_config.h**

After `QUEUE_DEPTH_CONFIG_RX` (line 122), add:

```c
#define QUEUE_DEPTH_MONITOR_TX  16

/* ---- Bus Monitor Protocol ---- */
#define MONITOR_HEADER_CAN_ID   0x604U
#define MONITOR_DATA_CAN_ID     0x605U
#define MONITOR_MAX_FILTER_IDS  32
```

- [x] **Step 2: Add section ID to config_protocol.h**

After `CFG_SECTION_DEVICE` (line 24), add:

```c
#define CFG_SECTION_MONITOR         0x06
```

- [x] **Step 3: Create bus_monitor.h**

```c
#ifndef BUS_MONITOR_H
#define BUS_MONITOR_H

#include <stdint.h>
#include <stdbool.h>
#include "can/can_bus.h"
#include "FreeRTOS.h"
#include "queue.h"

/* Filter modes */
#define MONITOR_FILTER_NONE      0
#define MONITOR_FILTER_WHITELIST 1
#define MONITOR_FILTER_BLACKLIST 2

/**
 * Initialize the bus monitor module.
 * @param monitor_tx_queue  FreeRTOS queue for monitor CAN frames (gateway_frame_t items)
 */
void bus_monitor_init(QueueHandle_t monitor_tx_queue);

/**
 * Enqueue a frame for monitoring. Called inline from can_task/lin_task.
 * If monitoring is disabled or the frame is filtered out, does nothing.
 * If the monitor TX queue is full, silently drops and increments drop count.
 */
void bus_monitor_enqueue_frame(const gateway_frame_t *gf);

/**
 * Drain monitor TX queue into the CAN TX path.
 * Called from can_task after the main TX queue is empty.
 * @param transmit_fn  Function to send a CAN frame on CAN1
 * @param max_frames   Max frames to drain per call (0 = unlimited)
 * @return Number of frames drained
 */
uint8_t bus_monitor_drain(bool (*transmit_fn)(const can_frame_t *frame),
                          uint8_t max_frames);

/* ---- Config Protocol Interface ---- */

/* Monitor params (READ_PARAM / WRITE_PARAM on SectionMonitor) */
#define MONITOR_PARAM_ENABLE     0  /* R/W, 1 byte: 0=off 1=on */
#define MONITOR_PARAM_BUS_MASK   1  /* R/W, 1 byte: bitmask (bit0=CAN1..bit5=LIN4) */
#define MONITOR_PARAM_FILTER_MODE 2 /* R/W, 1 byte: 0=none, 1=whitelist, 2=blacklist */
#define MONITOR_PARAM_DROP_COUNT 3  /* R, 1 byte: wrapping drop counter */

void bus_monitor_set_enabled(bool enabled);
bool bus_monitor_get_enabled(void);

void bus_monitor_set_bus_mask(uint8_t mask);
uint8_t bus_monitor_get_bus_mask(void);

void bus_monitor_set_filter_mode(uint8_t mode);
uint8_t bus_monitor_get_filter_mode(void);

uint8_t bus_monitor_get_drop_count(void);

/**
 * Load ID filter list via bulk write.
 * @param ids   Array of 32-bit CAN/LIN IDs
 * @param count Number of IDs (max MONITOR_MAX_FILTER_IDS)
 */
void bus_monitor_set_filter_ids(const uint32_t *ids, uint8_t count);

uint8_t bus_monitor_get_filter_id_count(void);

#endif /* BUS_MONITOR_H */
```

- [x] **Step 4: Create bus_monitor.c**

```c
#include "monitor/bus_monitor.h"
#include "board_config.h"

#include "FreeRTOS.h"
#include "task.h"
#include "hardware/timer.h"

#include <string.h>

/* ---- State (RAM-only, not persisted to NVM) ---- */

static QueueHandle_t s_monitor_tx_queue;
static volatile bool s_enabled;
static uint8_t  s_bus_mask;       /* bit0=CAN1, bit1=CAN2, bit2-5=LIN1-4 */
static uint8_t  s_filter_mode;    /* 0=none, 1=whitelist, 2=blacklist */
static uint8_t  s_drop_count;     /* wrapping counter */
static uint8_t  s_sequence;       /* 0-255, wrapping, pairs header with data */
static uint32_t s_start_timestamp; /* timestamp at monitor enable, for delta */

static uint32_t s_filter_ids[MONITOR_MAX_FILTER_IDS];
static uint8_t  s_filter_id_count;

/* ---- ID Exclusion ---- */

static bool is_excluded_id(uint32_t id)
{
    /* Never mirror config protocol (0x600-0x605), diagnostics (0x7F0-0x7F4),
     * or bootloader frames (0x700+) */
    if (id >= CONFIG_CAN_CMD_ID && id <= MONITOR_DATA_CAN_ID) return true;
    if (id >= DIAG_DEFAULT_CAN_ID && id <= DIAG_SYS_HEALTH_CAN_ID) return true;
    if (id >= BL_CAN_CMD_ID) return true;
    return false;
}

/* ---- ID Filter ---- */

static bool passes_id_filter(uint32_t id)
{
    if (s_filter_mode == MONITOR_FILTER_NONE) return true;

    bool found = false;
    for (uint8_t i = 0; i < s_filter_id_count; i++) {
        if (s_filter_ids[i] == id) {
            found = true;
            break;
        }
    }

    if (s_filter_mode == MONITOR_FILTER_WHITELIST) return found;
    /* BLACKLIST */
    return !found;
}

/* ---- Bus Filter ---- */

static bool passes_bus_filter(bus_id_t bus)
{
    if (bus >= BUS_COUNT) return false;
    return (s_bus_mask & (1U << bus)) != 0;
}

/* ---- Frame Encoding ---- */

/*
 * Header frame (0x604, 8 bytes):
 *   Byte 0:    sequence number (0-255)
 *   Byte 1:    source bus [3:0] + flags [7:4] (bit4=extended)
 *   Bytes 2-5: original frame ID (32-bit LE)
 *   Byte 6:    original DLC [3:0] + data[7] lower nibble [7:4]
 *              NOTE: For DLC=8, only 4 bits of data[7] survive (0x00-0x0F).
 *              This is a deliberate tradeoff to fit into 2 CAN frames.
 *   Byte 7:    timestamp delta low byte (ms mod 256, relative to monitor start)
 *
 * Data frame (0x605, up to 8 bytes):
 *   Byte 0:    sequence number (matches header)
 *   Bytes 1-7: original data[0..6]
 */

static void encode_monitor_frames(const gateway_frame_t *gf,
                                  can_frame_t *header_out,
                                  can_frame_t *data_out,
                                  bool *has_data)
{
    uint8_t seq = s_sequence++;
    uint8_t dlc = gf->frame.dlc;
    if (dlc > 8) dlc = 8;

    /* Header frame */
    header_out->id = MONITOR_HEADER_CAN_ID;
    header_out->dlc = 8;
    header_out->flags = 0;
    memset(header_out->data, 0, 8);

    header_out->data[0] = seq;
    header_out->data[1] = ((uint8_t)gf->source_bus & 0x0F)
                        | ((gf->frame.flags & CAN_FLAG_EFF) ? 0x10 : 0x00);
    header_out->data[2] = (uint8_t)(gf->frame.id);
    header_out->data[3] = (uint8_t)(gf->frame.id >> 8);
    header_out->data[4] = (uint8_t)(gf->frame.id >> 16);
    header_out->data[5] = (uint8_t)(gf->frame.id >> 24);
    header_out->data[6] = (dlc & 0x0F)
                        | ((dlc == 8) ? ((gf->frame.data[7] & 0x0F) << 4) : 0);
    uint32_t delta_ms = gf->timestamp - s_start_timestamp;
    header_out->data[7] = (uint8_t)(delta_ms & 0xFF);

    /* Data frame (only if DLC > 0) */
    *has_data = (dlc > 0);
    if (*has_data) {
        data_out->id = MONITOR_DATA_CAN_ID;
        data_out->flags = 0;
        memset(data_out->data, 0, 8);
        data_out->data[0] = seq;
        uint8_t copy_len = (dlc <= 7) ? dlc : 7;
        memcpy(&data_out->data[1], gf->frame.data, copy_len);
        data_out->dlc = 1 + copy_len;
    }
}

/* ---- Public API ---- */

void bus_monitor_init(QueueHandle_t monitor_tx_queue)
{
    s_monitor_tx_queue = monitor_tx_queue;
    s_enabled = false;
    s_bus_mask = 0x3F;   /* All buses enabled by default */
    s_filter_mode = MONITOR_FILTER_NONE;
    s_drop_count = 0;
    s_sequence = 0;
    s_start_timestamp = 0;
    s_filter_id_count = 0;
}

void bus_monitor_enqueue_frame(const gateway_frame_t *gf)
{
    if (!s_enabled) return;
    if (!passes_bus_filter(gf->source_bus)) return;
    if (is_excluded_id(gf->frame.id)) return;
    if (!passes_id_filter(gf->frame.id)) return;

    can_frame_t header, data;
    bool has_data;
    encode_monitor_frames(gf, &header, &data, &has_data);

    /* Queue header — gateway_frame_t wrapper for the CAN TX path */
    gateway_frame_t mon_gf;
    mon_gf.source_bus = BUS_CAN1;
    mon_gf.timestamp = 0;

    mon_gf.frame = header;
    if (xQueueSend(s_monitor_tx_queue, &mon_gf, 0) != pdTRUE) {
        s_drop_count++;
        return; /* Drop both header and data if header can't be queued */
    }

    if (has_data) {
        mon_gf.frame = data;
        if (xQueueSend(s_monitor_tx_queue, &mon_gf, 0) != pdTRUE) {
            s_drop_count++;
            /* Header was already queued — config tool will see a header without
             * matching data. The sequence number will still pair correctly on
             * the next complete frame, so this is a transient glitch, not fatal. */
        }
    }
}

uint8_t bus_monitor_drain(bool (*transmit_fn)(const can_frame_t *frame),
                          uint8_t max_frames)
{
    uint8_t drained = 0;
    gateway_frame_t mon_gf;
    while (xQueueReceive(s_monitor_tx_queue, &mon_gf, 0) == pdTRUE) {
        transmit_fn(&mon_gf.frame);
        drained++;
        if (max_frames > 0 && drained >= max_frames) break;
    }
    return drained;
}

/* ---- Config Protocol Accessors ---- */

void bus_monitor_set_enabled(bool enabled)
{
    if (enabled && !s_enabled) {
        /* Record start time on enable transition */
        s_start_timestamp = time_us_32() / 1000;
        s_sequence = 0;
        s_drop_count = 0;
    }
    s_enabled = enabled;
}
bool bus_monitor_get_enabled(void)           { return s_enabled; }

void bus_monitor_set_bus_mask(uint8_t mask)   { s_bus_mask = mask; }
uint8_t bus_monitor_get_bus_mask(void)        { return s_bus_mask; }

void bus_monitor_set_filter_mode(uint8_t mode)
{
    if (mode <= MONITOR_FILTER_BLACKLIST)
        s_filter_mode = mode;
}
uint8_t bus_monitor_get_filter_mode(void) { return s_filter_mode; }

uint8_t bus_monitor_get_drop_count(void) { return s_drop_count; }

void bus_monitor_set_filter_ids(const uint32_t *ids, uint8_t count)
{
    if (count > MONITOR_MAX_FILTER_IDS)
        count = MONITOR_MAX_FILTER_IDS;
    memcpy(s_filter_ids, ids, count * sizeof(uint32_t));
    s_filter_id_count = count;
}

uint8_t bus_monitor_get_filter_id_count(void) { return s_filter_id_count; }
```

- [x] **Step 5: Add source to CMakeLists.txt**

In `firmware/CMakeLists.txt`, add `src/monitor/bus_monitor.c` in **two places**:

1. In the `add_executable(${PROJECT_NAME} ...)` block (after line 92 `src/config/config_handler.c`), add:
```cmake
    # Bus Monitor Protocol
    src/monitor/bus_monitor.c
```

2. In the `COMMON_SOURCES` list (after line 156 `src/config/config_handler.c`), add:
```cmake
    src/monitor/bus_monitor.c
```

Both are needed — the main firmware and all test firmware targets (`test_phase1` through `test_phase6`) link against `can_manager.c` and `lin_manager.c` which will call `bus_monitor_enqueue_frame()`, so the linker needs `bus_monitor.c` in `COMMON_SOURCES` too.

- [x] **Step 6: Verify firmware compiles**

Run:
```bash
cd firmware && cmake --build build
```
Expected: Compile succeeds (bus_monitor.c has no callers yet, but must compile cleanly).

- [x] **Step 7: Commit**

```bash
git add firmware/src/monitor/ firmware/include/board_config.h firmware/src/config/config_protocol.h firmware/CMakeLists.txt
git commit -m "feat(firmware): add bus_monitor module — frame encoding, queue, filtering"
```

---

## Task 2: Firmware — Hook Monitor into CAN Task and Main

**Files:**
- Modify: `firmware/src/can/can_manager.c:137-149` (init), `270-315` (task loop)
- Modify: `firmware/src/can/can_manager.h` (API change)
- Modify: `firmware/src/main.c:138-153` (queue creation, init call)

### Step-by-step:

- [x] **Step 1: Update can_manager_init to accept monitor queue**

In `firmware/src/can/can_manager.h`, change the init signature:

```c
void can_manager_init(QueueHandle_t gateway_queue,
                      QueueHandle_t config_queue,
                      QueueHandle_t can_tx_queue,
                      QueueHandle_t monitor_tx_queue);
```

In `firmware/src/can/can_manager.c`, add a new static variable:

```c
static QueueHandle_t s_monitor_tx_queue;
```

Update `can_manager_init()` to accept and store the 4th parameter.

- [x] **Step 2: Add monitor enqueue calls in can_task_entry**

In the CAN1 RX drain loop, add the monitor call **after** the if/else block (after line 296, outside both branches, just before the closing `}` of the `while` loop):

```c
            if (is_config_frame(&gf)) {
                xQueueSend(s_config_queue, &gf, 0);
            } else {
                xQueueSend(s_gateway_queue, &gf, 0);
            }
            bus_monitor_enqueue_frame(&gf);  /* <-- add here */
```

In the CAN2 RX drain loop (after line 304 `xQueueSend(s_gateway_queue, &gf, 0)`), add:

```c
            xQueueSend(s_gateway_queue, &gf, 0);
            bus_monitor_enqueue_frame(&gf);  /* <-- add here */
```

The call is outside the if/else so it sees all frames. Config/diag/bootloader IDs are excluded internally by `is_excluded_id()` in `bus_monitor_enqueue_frame()`.

- [x] **Step 3: Add monitor TX drain after main TX queue**

After the existing TX drain loop (line 313), add:

```c
        /* Drain monitor TX queue (lower priority than application traffic) */
        bus_monitor_drain(&can_manager_transmit_can1, 4);
```

Add a static helper above `can_task_entry()`:

```c
static bool can_manager_transmit_can1(const can_frame_t *frame)
{
    return can_manager_transmit(CAN_BUS_1, frame);
}
```

The `max_frames=4` limit prevents monitor drain from blocking the main loop for too long. Monitor gets up to 4 frames per 1ms cycle.

- [x] **Step 4: Update main.c to create monitor queue and pass to init**

After the existing queue creation (line 142), add:

```c
    QueueHandle_t g_monitor_tx_queue = xQueueCreate(QUEUE_DEPTH_MONITOR_TX, sizeof(gateway_frame_t));
    ASSERT_ALLOC(g_monitor_tx_queue);
```

Update `can_manager_init` call (line 153):

```c
    can_manager_init(g_gateway_input_queue, g_config_rx_queue, g_can_tx_queue, g_monitor_tx_queue);
```

Add `bus_monitor_init` call after `can_manager_init`:

```c
    bus_monitor_init(g_monitor_tx_queue);
```

Add include at top of main.c:

```c
#include "monitor/bus_monitor.h"
```

- [x] **Step 5: Verify firmware compiles**

Run:
```bash
cd firmware && cmake --build build
```

- [x] **Step 6: Commit**

```bash
git add firmware/src/can/can_manager.c firmware/src/can/can_manager.h firmware/src/main.c
git commit -m "feat(firmware): hook bus monitor into CAN task — enqueue RX, drain TX"
```

---

## Task 3: Firmware — Hook Monitor into LIN Task

**Files:**
- Modify: `firmware/src/lin/lin_manager.c:100-124` (process_channel_interrupt)

### Step-by-step:

- [x] **Step 1: Add include**

At the top of `lin_manager.c`, add:

```c
#include "monitor/bus_monitor.h"
```

- [x] **Step 2: Add monitor enqueue in process_channel_interrupt**

In `process_channel_interrupt()`, after the `xQueueSend(s_gateway_queue, &gf, 0)` call (line 123), add:

```c
            bus_monitor_enqueue_frame(&gf);
```

This reuses the same `gateway_frame_t gf` that was just built for the gateway queue — it already has the correct source bus tag (BUS_LIN1 + ch) and timestamp.

- [x] **Step 3: Verify firmware compiles**

Run:
```bash
cd firmware && cmake --build build
```

- [x] **Step 4: Commit**

```bash
git add firmware/src/lin/lin_manager.c
git commit -m "feat(firmware): hook bus monitor into LIN task"
```

---

## Task 4: Firmware — Config Protocol Handler for SectionMonitor

**Files:**
- Modify: `firmware/src/config/config_handler.c:176-358` (handle_read_param), `360-501` (handle_write_param), `505-612` (bulk handlers)

### Step-by-step:

- [x] **Step 1: Add include**

At the top of `config_handler.c`, add:

```c
#include "monitor/bus_monitor.h"
```

- [x] **Step 2: Add READ_PARAM case for SectionMonitor**

In `handle_read_param()`, after the `CFG_SECTION_DEVICE` case block (around line 350), add:

```c
    case CFG_SECTION_MONITOR:
        switch (param) {
        case MONITOR_PARAM_ENABLE:
            payload[3] = bus_monitor_get_enabled() ? 1 : 0;
            plen = 4;
            break;
        case MONITOR_PARAM_BUS_MASK:
            payload[3] = bus_monitor_get_bus_mask();
            plen = 4;
            break;
        case MONITOR_PARAM_FILTER_MODE:
            payload[3] = bus_monitor_get_filter_mode();
            plen = 4;
            break;
        case MONITOR_PARAM_DROP_COUNT:
            payload[3] = bus_monitor_get_drop_count();
            plen = 4;
            break;
        default:
            send_response(CFG_CMD_READ_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        break;
```

- [x] **Step 3: Add WRITE_PARAM case for SectionMonitor**

In `handle_write_param()`, after the last section case, add:

```c
    case CFG_SECTION_MONITOR:
        if (dlc < 5) {
            send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        switch (param) {
        case MONITOR_PARAM_ENABLE:
            bus_monitor_set_enabled(data[4] != 0);
            break;
        case MONITOR_PARAM_BUS_MASK:
            bus_monitor_set_bus_mask(data[4]);
            break;
        case MONITOR_PARAM_FILTER_MODE:
            bus_monitor_set_filter_mode(data[4]);
            break;
        default:
            send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_OK, NULL, 0);
        return;
```

- [x] **Step 4: Add BULK_START size validation for SectionMonitor**

In `handle_bulk_start()`, add a size check for the monitor section. Find the size validation block (around line 520) and add after the existing section checks:

```c
    if (section == CFG_SECTION_MONITOR &&
        s_bulk_expected_size > MONITOR_MAX_FILTER_IDS * sizeof(uint32_t)) {
        send_response(CFG_CMD_BULK_START, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }
```

- [x] **Step 5: Add BULK_END case for SectionMonitor (ID filter list)**

In `handle_bulk_end()`, after the existing section cases, add a case for `CFG_SECTION_MONITOR`:

```c
    case CFG_SECTION_MONITOR:
    {
        /* Bulk data is an array of uint32_t IDs */
        uint8_t count = s_bulk_received / 4;
        if (count > MONITOR_MAX_FILTER_IDS)
            count = MONITOR_MAX_FILTER_IDS;
        bus_monitor_set_filter_ids((const uint32_t *)s_bulk_buffer, count);
        break;
    }
```

- [x] **Step 6: Verify firmware compiles**

Run:
```bash
cd firmware && cmake --build build
```

- [x] **Step 7: Commit**

```bash
git add firmware/src/config/config_handler.c
git commit -m "feat(firmware): add SectionMonitor config protocol handler"
```

---

## Task 5: Config Tool — MonitorFrameDecoder

**Files:**
- Create: `software/CanLinConfig/Services/MonitorFrameDecoder.cs`
- Create: `software/CanLinConfig.Tests/MonitorFrameDecoderTests.cs`

### Step-by-step:

- [x] **Step 1: Write tests for MonitorFrameDecoder**

```csharp
// software/CanLinConfig.Tests/MonitorFrameDecoderTests.cs
using CanLinConfig.Adapters;
using CanLinConfig.Models;
using CanLinConfig.Services;

namespace CanLinConfig.Tests;

public class MonitorFrameDecoderTests
{
    private static CanFrame MakeHeader(byte seq, byte bus, uint id, byte dlc,
        byte data7 = 0, byte tsDelta = 0, bool extended = false)
    {
        var frame = new CanFrame { Id = 0x604, Dlc = 8 };
        frame.Data[0] = seq;
        frame.Data[1] = (byte)((bus & 0x0F) | (extended ? 0x10 : 0x00));
        frame.Data[2] = (byte)(id);
        frame.Data[3] = (byte)(id >> 8);
        frame.Data[4] = (byte)(id >> 16);
        frame.Data[5] = (byte)(id >> 24);
        frame.Data[6] = (byte)((dlc & 0x0F) | ((dlc == 8 ? (data7 & 0x0F) : 0) << 4));
        frame.Data[7] = tsDelta;
        return frame;
    }

    private static CanFrame MakeData(byte seq, byte[] payload)
    {
        var frame = new CanFrame { Id = 0x605, Dlc = (byte)(1 + payload.Length) };
        frame.Data[0] = seq;
        Array.Copy(payload, 0, frame.Data, 1, payload.Length);
        return frame;
    }

    [Fact]
    public void Decode_standard_CAN2_frame()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        decoder.OnCanFrame(MakeHeader(0, 1, 0x123, 4));
        decoder.OnCanFrame(MakeData(0, [0xAA, 0xBB, 0xCC, 0xDD]));

        Assert.NotNull(result);
        Assert.Equal(BusFrame.Bus.CAN2, result!.SourceBus);
        Assert.Equal(0x123u, result.Id);
        Assert.Equal(4, result.Dlc);
        Assert.Equal(0xAA, result.Data[0]);
        Assert.Equal(0xDD, result.Data[3]);
        Assert.False(result.IsExtended);
    }

    [Fact]
    public void Decode_DLC8_frame_with_data7_in_header()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        decoder.OnCanFrame(MakeHeader(5, 0, 0x200, 8, data7: 0xAB));
        decoder.OnCanFrame(MakeData(5, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]));

        Assert.NotNull(result);
        Assert.Equal(8, result!.Dlc);
        Assert.Equal(0x07, result.Data[6]);
        // data[7] comes from header byte 6 upper nibble: 0xAB & 0x0F = 0x0B
        Assert.Equal(0x0B, result.Data[7]);
    }

    [Fact]
    public void Decode_DLC0_header_only()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        decoder.OnCanFrame(MakeHeader(10, 2, 0x3C, 0));

        Assert.NotNull(result);
        Assert.Equal(BusFrame.Bus.LIN1, result!.SourceBus);
        Assert.Equal(0x3Cu, result.Id);
        Assert.Equal(0, result.Dlc);
    }

    [Fact]
    public void Decode_LIN4_frame()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        decoder.OnCanFrame(MakeHeader(0, 5, 0x1A, 3));
        decoder.OnCanFrame(MakeData(0, [0x10, 0x20, 0x30]));

        Assert.NotNull(result);
        Assert.Equal(BusFrame.Bus.LIN4, result!.SourceBus);
    }

    [Fact]
    public void Decode_extended_flag()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        decoder.OnCanFrame(MakeHeader(0, 0, 0x1FFFFFFF, 2, extended: true));
        decoder.OnCanFrame(MakeData(0, [0xAA, 0xBB]));

        Assert.NotNull(result);
        Assert.True(result!.IsExtended);
        Assert.Equal(0x1FFFFFFFu, result.Id);
    }

    [Fact]
    public void Sequence_gap_increments_counter()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        // Seq 0
        decoder.OnCanFrame(MakeHeader(0, 1, 0x100, 2));
        decoder.OnCanFrame(MakeData(0, [0x01, 0x02]));
        Assert.NotNull(result);
        Assert.Equal(0u, decoder.SequenceGapCount);

        // Seq 2 (gap — seq 1 missing)
        result = null;
        decoder.OnCanFrame(MakeHeader(2, 1, 0x101, 2));
        decoder.OnCanFrame(MakeData(2, [0x03, 0x04]));
        Assert.NotNull(result);
        Assert.Equal(1u, decoder.SequenceGapCount);
    }

    [Fact]
    public void Sequence_wraps_at_255()
    {
        var decoder = new MonitorFrameDecoder();
        int count = 0;
        decoder.FrameDecoded += (_, _) => count++;

        // Feed seq 254, 255, 0 — no gaps
        decoder.OnCanFrame(MakeHeader(254, 0, 0x100, 1));
        decoder.OnCanFrame(MakeData(254, [0x01]));
        decoder.OnCanFrame(MakeHeader(255, 0, 0x100, 1));
        decoder.OnCanFrame(MakeData(255, [0x02]));
        decoder.OnCanFrame(MakeHeader(0, 0, 0x100, 1));
        decoder.OnCanFrame(MakeData(0, [0x03]));

        Assert.Equal(3, count);
        Assert.Equal(0u, decoder.SequenceGapCount);
    }

    [Fact]
    public void Mismatched_data_seq_is_discarded()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        decoder.OnCanFrame(MakeHeader(0, 1, 0x100, 4));
        // Data with wrong seq — should be discarded
        decoder.OnCanFrame(MakeData(5, [0x01, 0x02, 0x03, 0x04]));

        Assert.Null(result);
    }

    [Fact]
    public void Non_monitor_frames_ignored()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        var frame = new CanFrame { Id = 0x100, Dlc = 8 };
        decoder.OnCanFrame(frame);

        Assert.Null(result);
    }

    [Fact]
    public void Consecutive_headers_overwrites_pending()
    {
        var decoder = new MonitorFrameDecoder();
        BusFrame? result = null;
        decoder.FrameDecoded += (_, f) => result = f;

        // Header seq 0 (DLC=4) — will be overwritten
        decoder.OnCanFrame(MakeHeader(0, 1, 0x100, 4));
        // Header seq 1 (DLC=2) — overwrites pending header
        decoder.OnCanFrame(MakeHeader(1, 1, 0x200, 2));
        // Data seq 1 — pairs with second header
        decoder.OnCanFrame(MakeData(1, [0xAA, 0xBB]));

        Assert.NotNull(result);
        Assert.Equal(0x200u, result!.Id);
        Assert.Equal(2, result.Dlc);
        // Seq 0 was lost — gap detected
        Assert.Equal(1u, decoder.SequenceGapCount);
    }
}
```

- [x] **Step 2: Run tests — verify they fail**

Run:
```bash
cd software && dotnet test CanLinConfig.Tests --filter "MonitorFrameDecoder" -v n
```
Expected: Compile error (MonitorFrameDecoder doesn't exist yet).

- [x] **Step 3: Implement MonitorFrameDecoder**

```csharp
// software/CanLinConfig/Services/MonitorFrameDecoder.cs
using CanLinConfig.Adapters;
using CanLinConfig.Models;
using CanLinConfig.Protocol;

namespace CanLinConfig.Services;

/// <summary>
/// Pairs monitor header (0x604) and data (0x605) frames by sequence number,
/// reconstructing the original BusFrame.
/// </summary>
public class MonitorFrameDecoder
{
    private CanFrame? _pendingHeader;
    private int _expectedSeq = -1;  // -1 = no expectation yet (first frame)
    private uint _seqGapCount;

    public event EventHandler<BusFrame>? FrameDecoded;

    public uint SequenceGapCount => _seqGapCount;

    public void Reset()
    {
        _pendingHeader = null;
        _expectedSeq = -1;
        _seqGapCount = 0;
    }

    public void OnCanFrame(CanFrame frame)
    {
        if (frame.Id == ProtocolConstants.MonitorHeaderId)
            HandleHeader(frame);
        else if (frame.Id == ProtocolConstants.MonitorDataId)
            HandleData(frame);
        // Other IDs silently ignored
    }

    private void HandleHeader(CanFrame header)
    {
        if (header.Dlc < 8) return;

        byte seq = header.Data[0];
        byte dlc = (byte)(header.Data[6] & 0x0F);

        // Check for sequence gap
        if (_expectedSeq >= 0 && seq != _expectedSeq)
        {
            _seqGapCount++;
        }

        if (dlc == 0)
        {
            // DLC=0 — header-only frame, no data frame expected
            var busFrame = DecodeHeaderOnly(header);
            _expectedSeq = (seq + 1) & 0xFF;
            _pendingHeader = null;
            FrameDecoded?.Invoke(this, busFrame);
        }
        else
        {
            // Store header, wait for matching data frame
            _pendingHeader = header;
        }
    }

    private void HandleData(CanFrame data)
    {
        if (_pendingHeader == null) return;

        byte headerSeq = _pendingHeader.Data[0];
        byte dataSeq = data.Data[0];

        if (dataSeq != headerSeq)
        {
            // Mismatched sequence — discard pending header
            _pendingHeader = null;
            return;
        }

        var busFrame = DecodeHeaderData(_pendingHeader, data);
        _expectedSeq = (headerSeq + 1) & 0xFF;
        _pendingHeader = null;
        FrameDecoded?.Invoke(this, busFrame);
    }

    private static BusFrame DecodeHeaderOnly(CanFrame header)
    {
        byte busId = (byte)(header.Data[1] & 0x0F);
        bool extended = (header.Data[1] & 0x10) != 0;
        uint id = (uint)(header.Data[2] | (header.Data[3] << 8) |
                         (header.Data[4] << 16) | (header.Data[5] << 24));

        return new BusFrame(
            (BusFrame.Bus)busId, id, 0, new byte[8],
            DateTime.Now, extended);
    }

    private static BusFrame DecodeHeaderData(CanFrame header, CanFrame data)
    {
        byte busId = (byte)(header.Data[1] & 0x0F);
        bool extended = (header.Data[1] & 0x10) != 0;
        uint id = (uint)(header.Data[2] | (header.Data[3] << 8) |
                         (header.Data[4] << 16) | (header.Data[5] << 24));
        byte dlc = (byte)(header.Data[6] & 0x0F);

        var payload = new byte[8];
        int copyLen = Math.Min(dlc, 7);
        if (data.Dlc > 1)
            Array.Copy(data.Data, 1, payload, 0, Math.Min(copyLen, data.Dlc - 1));

        // For DLC=8, data[7] is in header byte 6 upper nibble
        if (dlc == 8)
            payload[7] = (byte)((header.Data[6] >> 4) & 0x0F);

        return new BusFrame(
            (BusFrame.Bus)busId, id, dlc, payload,
            DateTime.Now, extended);
    }
}
```

- [x] **Step 4: Run tests — verify they pass**

Run:
```bash
cd software && dotnet test CanLinConfig.Tests --filter "MonitorFrameDecoder" -v n
```
Expected: All 10 tests PASS.

- [x] **Step 5: Commit**

```bash
git add software/CanLinConfig/Services/MonitorFrameDecoder.cs software/CanLinConfig.Tests/MonitorFrameDecoderTests.cs
git commit -m "feat(config-tool): add MonitorFrameDecoder — pairs 0x604/0x605 into BusFrames"
```

---

## Task 6: Config Tool — Wire Monitor Frames into Pipeline

**Files:**
- Modify: `software/CanLinConfig/Protocol/ProtocolConstants.cs:48` (add section constant)
- Modify: `software/CanLinConfig/Protocol/ConfigProtocol.cs:36-51` (intercept monitor IDs)
- Modify: `software/CanLinConfig/ViewModels/MainViewModel.cs:145-149` (wire decoder)

### Step-by-step:

- [x] **Step 1: Add SectionMonitor constant to ProtocolConstants.cs**

After `SectionDevice` (line 48), add:

```csharp
    public const byte SectionMonitor = 0x06;

    // Monitor param indices (SectionMonitor READ_PARAM/WRITE_PARAM)
    public const byte MonitorParamEnable = 0;
    public const byte MonitorParamBusMask = 1;
    public const byte MonitorParamFilterMode = 2;
    public const byte MonitorParamDropCount = 3;

    // Monitor filter modes
    public const byte MonitorFilterNone = 0;
    public const byte MonitorFilterWhitelist = 1;
    public const byte MonitorFilterBlacklist = 2;

    // Monitor limits
    public const int MonitorMaxFilterIds = 32;
```

- [x] **Step 2: Add MonitorFrameReceived event to ConfigProtocol**

In `ConfigProtocol.cs`, add a new event alongside `RawFrameReceived` (line 28):

```csharp
    public event EventHandler<CanFrameEventArgs>? MonitorFrameReceived;
```

In `OnFrameReceived()` (lines 36-51), add handling for monitor IDs before the existing response handling:

```csharp
    private void OnFrameReceived(object? sender, CanFrameEventArgs e)
    {
        // Monitor frames are high-frequency — route directly, don't raise RawFrameReceived
        if (e.Frame.Id == ProtocolConstants.MonitorHeaderId ||
            e.Frame.Id == ProtocolConstants.MonitorDataId)
        {
            MonitorFrameReceived?.Invoke(this, e);
            return;
        }

        RawFrameReceived?.Invoke(this, e);

        if (e.Frame.Id == ProtocolConstants.ConfigRespId)
        {
            if (_pendingResponse != null && e.Frame.Data[0] == _expectedCmd)
            {
                _pendingResponse.TrySetResult(e.Frame);
            }
        }
        else if (e.Frame.Id == ProtocolConstants.ConfigBulkRespId)
        {
            HandleBulkReadData(e.Frame);
        }
    }
```

- [x] **Step 3: Wire MonitorFrameDecoder in MainViewModel**

In `MainViewModel.cs`, add a field:

```csharp
    private MonitorFrameDecoder? _monitorDecoder;
```

In the connection setup (around lines 145-149), after creating `_protocol`, add:

```csharp
_monitorDecoder = new MonitorFrameDecoder();
_monitorDecoder.FrameDecoded += (_, busFrame) => BusDataService.OnFrame(busFrame);
_protocol.MonitorFrameReceived += (_, e) => _monitorDecoder.OnCanFrame(e.Frame);
```

In the `Disconnect()` method, add cleanup:

```csharp
_monitorDecoder = null;
```

Add the using:

```csharp
using CanLinConfig.Services;
```

- [x] **Step 4: Build the solution**

Run:
```bash
cd software && dotnet build CanLinConfig.sln
```
Expected: Build succeeds.

- [x] **Step 5: Run all tests**

Run:
```bash
cd software && dotnet test CanLinConfig.Tests -v n
```
Expected: All existing tests + MonitorFrameDecoder tests pass.

- [x] **Step 6: Commit**

```bash
git add software/CanLinConfig/Protocol/ProtocolConstants.cs software/CanLinConfig/Protocol/ConfigProtocol.cs software/CanLinConfig/ViewModels/MainViewModel.cs
git commit -m "feat(config-tool): wire monitor frames through pipeline into BusDataService"
```

---

## Task 7: Config Tool — MonitorControlViewModel

**Files:**
- Create: `software/CanLinConfig/ViewModels/MonitorControlViewModel.cs`

### Step-by-step:

- [x] **Step 1: Create MonitorControlViewModel**

```csharp
// software/CanLinConfig/ViewModels/MonitorControlViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Protocol;
using CanLinConfig.Services;

namespace CanLinConfig.ViewModels;

public partial class MonitorControlViewModel : ObservableObject
{
    private ConfigProtocol? _protocol;
    private MonitorFrameDecoder? _decoder;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _monitorCan1 = true;
    [ObservableProperty] private bool _monitorCan2 = true;
    [ObservableProperty] private bool _monitorLin1 = true;
    [ObservableProperty] private bool _monitorLin2 = true;
    [ObservableProperty] private bool _monitorLin3 = true;
    [ObservableProperty] private bool _monitorLin4 = true;
    [ObservableProperty] private uint _sequenceGaps;
    [ObservableProperty] private uint _dropCount;
    [ObservableProperty] private bool _isConnected;

    public void SetProtocol(ConfigProtocol? protocol, MonitorFrameDecoder? decoder)
    {
        _protocol = protocol;
        _decoder = decoder;
        IsConnected = protocol != null;
        if (!IsConnected)
        {
            IsEnabled = false;
            SequenceGaps = 0;
            DropCount = 0;
        }
    }

    public void UpdateStats()
    {
        if (_decoder != null)
            SequenceGaps = _decoder.SequenceGapCount;
    }

    partial void OnIsEnabledChanged(bool value) => _ = SendEnableAsync(value);

    partial void OnMonitorCan1Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnMonitorCan2Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnMonitorLin1Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnMonitorLin2Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnMonitorLin3Changed(bool value) => _ = SendBusMaskAsync();
    partial void OnMonitorLin4Changed(bool value) => _ = SendBusMaskAsync();

    private byte BuildBusMask()
    {
        byte mask = 0;
        if (MonitorCan1) mask |= 0x01;
        if (MonitorCan2) mask |= 0x02;
        if (MonitorLin1) mask |= 0x04;
        if (MonitorLin2) mask |= 0x08;
        if (MonitorLin3) mask |= 0x10;
        if (MonitorLin4) mask |= 0x20;
        return mask;
    }

    private async Task SendEnableAsync(bool enabled)
    {
        if (_protocol == null) return;
        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionMonitor,
            ProtocolConstants.MonitorParamEnable, 0,
            [(byte)(enabled ? 1 : 0)]);
    }

    private async Task SendBusMaskAsync()
    {
        if (_protocol == null) return;
        await _protocol.WriteParamAsync(
            ProtocolConstants.SectionMonitor,
            ProtocolConstants.MonitorParamBusMask, 0,
            [BuildBusMask()]);
    }

    [RelayCommand]
    private async Task RefreshDropCount()
    {
        if (_protocol == null) return;
        var result = await _protocol.ReadParamAsync(
            ProtocolConstants.SectionMonitor,
            ProtocolConstants.MonitorParamDropCount, 0);
        if (result.Success && result.Value.Length > 0)
            DropCount = result.Value[0];
    }
}
```

- [x] **Step 2: Build the solution**

Run:
```bash
cd software && dotnet build CanLinConfig.sln
```

- [x] **Step 3: Commit**

```bash
git add software/CanLinConfig/ViewModels/MonitorControlViewModel.cs
git commit -m "feat(config-tool): add MonitorControlViewModel — enable, bus mask, drop count"
```

---

## Task 8: Config Tool — MonitorControlPanel XAML

**Files:**
- Create: `software/CanLinConfig/Views/MonitorControlPanel.xaml`
- Create: `software/CanLinConfig/Views/MonitorControlPanel.xaml.cs`
- Modify: `software/CanLinConfig/Views/BusMonitorView.xaml` (add panel)
- Modify: `software/CanLinConfig/ViewModels/BusMonitorViewModel.cs` (add sub-VM)

### Step-by-step:

- [x] **Step 1: Create MonitorControlPanel.xaml**

```xml
<UserControl x:Class="CanLinConfig.Views.MonitorControlPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Border BorderBrush="{DynamicResource MahApps.Brushes.Gray7}" BorderThickness="0,0,0,1" Padding="5,3">
        <StackPanel Orientation="Horizontal">
            <TextBlock Text="Monitor:" VerticalAlignment="Center" Margin="0,0,5,0" FontWeight="SemiBold"/>
            <CheckBox Content="Enable" IsChecked="{Binding IsEnabled}"
                      IsEnabled="{Binding IsConnected}" VerticalAlignment="Center" Margin="0,0,10,0"/>
            <Separator Style="{StaticResource {x:Static ToolBar.SeparatorStyleKey}}" Margin="5,0"/>
            <TextBlock Text="Buses:" VerticalAlignment="Center" Margin="5,0,5,0"/>
            <CheckBox Content="CAN1" IsChecked="{Binding MonitorCan1}" VerticalAlignment="Center" Margin="0,0,4,0"/>
            <CheckBox Content="CAN2" IsChecked="{Binding MonitorCan2}" VerticalAlignment="Center" Margin="0,0,4,0"/>
            <CheckBox Content="LIN1" IsChecked="{Binding MonitorLin1}" VerticalAlignment="Center" Margin="0,0,4,0"/>
            <CheckBox Content="LIN2" IsChecked="{Binding MonitorLin2}" VerticalAlignment="Center" Margin="0,0,4,0"/>
            <CheckBox Content="LIN3" IsChecked="{Binding MonitorLin3}" VerticalAlignment="Center" Margin="0,0,4,0"/>
            <CheckBox Content="LIN4" IsChecked="{Binding MonitorLin4}" VerticalAlignment="Center" Margin="0,0,10,0"/>
            <Separator Style="{StaticResource {x:Static ToolBar.SeparatorStyleKey}}" Margin="5,0"/>
            <TextBlock VerticalAlignment="Center" Margin="5,0,0,0">
                <Run Text="Gaps:"/>
                <Run Text="{Binding SequenceGaps, Mode=OneWay}"/>
                <Run Text=" Drops:"/>
                <Run Text="{Binding DropCount, Mode=OneWay}"/>
            </TextBlock>
        </StackPanel>
    </Border>
</UserControl>
```

- [x] **Step 2: Create MonitorControlPanel.xaml.cs**

```csharp
namespace CanLinConfig.Views;

public partial class MonitorControlPanel
{
    public MonitorControlPanel()
    {
        InitializeComponent();
    }
}
```

- [x] **Step 3: Add MonitorControl to BusMonitorViewModel**

In `BusMonitorViewModel.cs`, add a property:

```csharp
    public MonitorControlViewModel MonitorControl { get; }
```

Initialize in constructor (after the Instruments line):

```csharp
        MonitorControl = new MonitorControlViewModel();
```

- [x] **Step 4: Add MonitorControlPanel to BusMonitorView.xaml**

Add a third row to the top-level Grid (between the database bar StackPanel and the main content Grid). Insert a new row definition `<RowDefinition Height="Auto"/>` after the first one, and shift the main content Grid to `Grid.Row="2"`.

After the database `StackPanel` (row 0), add:

```xml
        <local:MonitorControlPanel Grid.Row="1" DataContext="{Binding MonitorControl}"/>
```

Update the main content Grid row to `Grid.Row="2"`:

```xml
        <Grid Grid.Row="2">
```

Update the RowDefinitions to have 3 rows:

```xml
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>
```

- [x] **Step 5: Wire MonitorControl in MainViewModel**

In MainViewModel's connection logic, after creating the MonitorFrameDecoder, pass protocol and decoder to the BusMonitor's MonitorControl:

```csharp
BusMonitor.MonitorControl.SetProtocol(_protocol, _monitorDecoder);
```

In `Disconnect()`, clean up:

```csharp
BusMonitor.MonitorControl.SetProtocol(null, null);
```

- [x] **Step 6: Build the solution**

Run:
```bash
cd software && dotnet build CanLinConfig.sln
```

- [x] **Step 7: Run all tests**

Run:
```bash
cd software && dotnet test CanLinConfig.Tests -v n
```
Expected: All tests pass.

- [x] **Step 8: Commit**

```bash
git add software/CanLinConfig/Views/MonitorControlPanel.xaml software/CanLinConfig/Views/MonitorControlPanel.xaml.cs software/CanLinConfig/ViewModels/MonitorControlViewModel.cs software/CanLinConfig/ViewModels/BusMonitorViewModel.cs software/CanLinConfig/Views/BusMonitorView.xaml software/CanLinConfig/ViewModels/MainViewModel.cs
git commit -m "feat(config-tool): add MonitorControlPanel — enable, bus checkboxes, gap/drop counters"
```

---

## Task 9: Update DBC, Docs, and Version

**Files:**
- Modify: `docs/CanLinBoard.dbc` (add monitor message definitions)
- Modify: `docs/bus-monitor-logger-master-plan.md` (update Plan 4 status)
- Modify: `firmware/include/board_config.h:7-9` (bump version to 0.3.0)

### Step-by-step:

- [x] **Step 1: Add monitor messages to DBC**

Add to `docs/CanLinBoard.dbc`:

```
BO_ 1540 MonitorHeader: 8 Gateway
 SG_ Sequence : 0|8@1+ (1,0) [0|255] "" Tool
 SG_ SourceBus : 8|4@1+ (1,0) [0|5] "" Tool
 SG_ ExtendedFlag : 12|1@1+ (1,0) [0|1] "" Tool
 SG_ OriginalId : 16|32@1+ (1,0) [0|536870911] "" Tool
 SG_ OriginalDlc : 48|4@1+ (1,0) [0|8] "" Tool
 SG_ Data7Nibble : 52|4@1+ (1,0) [0|15] "" Tool
 SG_ TimestampDelta : 56|8@1+ (1,0) [0|255] "ms" Tool

BO_ 1541 MonitorData: 8 Gateway
 SG_ Sequence : 0|8@1+ (1,0) [0|255] "" Tool
 SG_ Payload : 8|56@1+ (1,0) [0|0] "" Tool
```

- [x] **Step 2: Update master plan tracker**

In `docs/bus-monitor-logger-master-plan.md`, update Plan 4 section:

```markdown
## Plan 4: Firmware Monitor Protocol
**Status:** COMPLETE
**Plan:** [`docs/superpowers/plans/2026-03-23-firmware-monitor-protocol.md`](superpowers/plans/2026-03-23-firmware-monitor-protocol.md)
```

Fill in commit count and test count after implementation.

- [x] **Step 3: Bump firmware version**

In `firmware/include/board_config.h`, update:

```c
#define FW_VERSION_MAJOR    0
#define FW_VERSION_MINOR    3
#define FW_VERSION_PATCH    0
```

- [x] **Step 4: Verify firmware builds**

Run:
```bash
cd firmware && cmake --build build
```

- [x] **Step 5: Run all config tool tests**

Run:
```bash
cd software && dotnet test CanLinConfig.Tests -v n
```

- [x] **Step 6: Commit**

```bash
git add docs/CanLinBoard.dbc docs/bus-monitor-logger-master-plan.md firmware/include/board_config.h
git commit -m "docs: update DBC with monitor messages, bump firmware to v0.3.0"
```

---

## Testing Notes

### Config tool (can test now)
- MonitorFrameDecoder unit tests cover: standard frames, DLC=0/8, extended flag, LIN buses, sequence gaps, wraps, mismatched sequences
- Integration: build succeeds, existing tests pass

### Firmware (deferred — requires hardware)
- Monitor enable/disable via config protocol
- Bus mask filtering (enable CAN2 only, verify only CAN2 frames mirrored)
- ID whitelist/blacklist via bulk write
- Monitor TX queue backpressure (flood, verify drops + drop count)
- Sequence number continuity at config tool
- Application traffic priority (monitor doesn't starve gateway frames)
- Stack watermark checks (can_task and lin_task have minimal added stack usage)
