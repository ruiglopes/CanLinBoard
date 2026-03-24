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
    s_bus_mask = 0x3E;   /* CAN2+LIN1-4 by default (CAN1 excluded — tool sees it natively) */
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
