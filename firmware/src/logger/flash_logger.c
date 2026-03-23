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
static volatile uint32_t s_drop_count;  /* frames dropped due to full queue */
static uint32_t s_trigger_offset;          /* write_offset when trigger fired */
static uint32_t s_post_trigger_remaining;  /* bytes left to capture after trigger */

/* ---- Forward Declarations ---- */

static void load_metadata(void);
static void save_metadata(void);
static void write_entry(const log_entry_t *entry);
static void flush_page_buffer(void);
static bool erase_sector_if_needed(uint32_t addr);
static bool wait_flash_done(uint32_t max_polls);

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

    if (xTaskGetSchedulerState() == taskSCHEDULER_RUNNING) {
        /* Non-blocking path — keep CAN PIO IRQ alive */
        uint32_t irq = sec_flash_acquire_bus();
        sec_flash_sector_erase_start(LOG_META_OFFSET);
        sec_flash_release_bus(irq);

        wait_flash_done(500); /* ~45ms erase, poll with vTaskDelay */

        irq = sec_flash_acquire_bus();
        sec_flash_page_program_start(LOG_META_OFFSET, (const uint8_t *)&s_meta, sizeof(s_meta));
        sec_flash_release_bus(irq);

        wait_flash_done(20); /* ~0.7ms program */
    } else {
        /* Blocking path — called from init before scheduler starts */
        uint32_t irq = sec_flash_acquire_bus();
        sec_flash_sector_erase(LOG_META_OFFSET);
        sec_flash_page_program(LOG_META_OFFSET, (const uint8_t *)&s_meta, sizeof(s_meta));
        sec_flash_release_bus(irq);
    }
}

/* ---- Non-blocking Flash Helpers ---- */

/*
 * Poll flash busy flag with interrupts ENABLED between polls.
 * Each poll: acquire bus (~us) → read status → release bus (~us).
 * Between polls: vTaskDelay(1) lets CAN PIO IRQ run freely.
 * This avoids the critical bug where blocking flash ops (sector erase
 * up to 400ms) mask CAN PIO IRQ and cause BUSHEAVY.
 */
static bool wait_flash_done(uint32_t max_polls)
{
    for (uint32_t i = 0; i < max_polls; i++) {
        vTaskDelay(1);  /* ~1ms with interrupts enabled — CAN IRQ runs */
        uint32_t irq = sec_flash_acquire_bus();
        bool busy = sec_flash_is_busy();
        sec_flash_release_bus(irq);
        if (!busy) return true;
    }
    return false;
}

static bool erase_sector_if_needed(uint32_t addr)
{
    /* Erase when we hit the start of a new sector */
    if ((addr & (NVM_SECTOR_SIZE - 1)) != 0)
        return true;  /* Not at sector boundary, no erase needed */

    /* Start erase — brief interrupt disable for SPI command only */
    uint32_t irq = sec_flash_acquire_bus();
    sec_flash_sector_erase_start(addr);
    sec_flash_release_bus(irq);

    /* Poll with interrupts enabled (~45ms typical, 400ms max) */
    if (!wait_flash_done(500)) {
        s_meta.flash_errors++;
        if (s_meta.flash_errors >= LOG_MAX_FLASH_ERRORS) {
            s_state = LOG_STATE_ERROR;
            s_meta.state = LOG_STATE_ERROR;
            save_metadata();
        }
        return false;
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

    /* Start page program — brief interrupt disable for SPI transfer */
    uint32_t irq = sec_flash_acquire_bus();
    sec_flash_page_program_start(s_meta.write_offset, s_page_buf, NVM_PAGE_SIZE);
    sec_flash_release_bus(irq);

    /* Poll with interrupts enabled (~0.7ms typical, 10ms max) */
    if (!wait_flash_done(20)) {
        s_meta.flash_errors++;
        if (s_meta.flash_errors >= LOG_MAX_FLASH_ERRORS) {
            s_state = LOG_STATE_ERROR;
            s_meta.state = LOG_STATE_ERROR;
            save_metadata();
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

/* ---- Trigger Check ---- */

static bool check_trigger(const log_entry_t *entry)
{
    /* Only check on the configured bus */
    if (entry->bus != s_meta.trigger_bus) return false;
    if (entry->frame_id != s_meta.trigger_id) return false;

    switch (s_meta.trigger_op) {
    case LOG_TRIGGER_OP_ANY:
        return true;
    case LOG_TRIGGER_OP_EQ:
        if (s_meta.trigger_byte >= entry->dlc) return false;
        return entry->data[s_meta.trigger_byte] == s_meta.trigger_value;
    case LOG_TRIGGER_OP_GT:
        if (s_meta.trigger_byte >= entry->dlc) return false;
        return entry->data[s_meta.trigger_byte] > s_meta.trigger_value;
    case LOG_TRIGGER_OP_LT:
        if (s_meta.trigger_byte >= entry->dlc) return false;
        return entry->data[s_meta.trigger_byte] < s_meta.trigger_value;
    case LOG_TRIGGER_OP_MASK:
        if (s_meta.trigger_byte >= entry->dlc) return false;
        return (entry->data[s_meta.trigger_byte] & s_meta.trigger_value) != 0;
    default:
        return false;
    }
}

/* ---- Public API ---- */

void flash_logger_init(QueueHandle_t log_queue)
{
    s_log_queue = log_queue;
    s_state = LOG_STATE_IDLE;
    s_page_buf_pos = 0;
    s_drop_count = 0;
    load_metadata();

    if (s_meta.state == LOG_STATE_RECORDING) {
        if (s_meta.mode == LOG_MODE_CONTINUOUS) {
            /* Continuous mode — auto-resume recording after reboot */
            s_state = LOG_STATE_RECORDING;
            /* Don't reset start_timestamp — keep relative timing continuous */
        } else {
            /* Manual mode — unclean shutdown, reset to idle */
            s_meta.state = LOG_STATE_IDLE;
            save_metadata();
        }
    } else if (s_meta.state == LOG_STATE_ARMED || s_meta.state == LOG_STATE_CAPTURING) {
        /* Triggered mode — don't auto-resume, reset to idle */
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
            uint8_t state = s_state;
            if (state != LOG_STATE_RECORDING && state != LOG_STATE_ARMED &&
                state != LOG_STATE_CAPTURING)
                continue;

            /* Check if frames were dropped since last write — insert gap marker */
            if (s_drop_count > 0) {
                log_entry_t gap;
                memset(&gap, 0, sizeof(gap));
                gap.timestamp_ms = gf.timestamp - s_meta.start_timestamp;
                gap.bus = 0xFF;
                gap.frame_id = s_drop_count;
                s_drop_count = 0;
                write_entry(&gap);
            }

            log_entry_t entry;
            entry.timestamp_ms = gf.timestamp - s_meta.start_timestamp;
            entry.frame_id = gf.frame.id;
            entry.bus = (uint8_t)gf.source_bus;
            entry.dlc = gf.frame.dlc;
            memcpy(entry.data, gf.frame.data, 8);
            entry.reserved = 0;

            write_entry(&entry);

            /* Triggered mode state machine */
            if (state == LOG_STATE_ARMED) {
                if (check_trigger(&entry)) {
                    s_trigger_offset = s_meta.write_offset;
                    s_post_trigger_remaining = s_meta.post_trigger_kb * 1024;
                    s_state = LOG_STATE_CAPTURING;
                    s_meta.state = LOG_STATE_CAPTURING;
                }
            } else if (state == LOG_STATE_CAPTURING) {
                if (s_post_trigger_remaining <= LOG_ENTRY_SIZE) {
                    /* Post-trigger capture complete — auto-stop */
                    s_state = LOG_STATE_IDLE;
                    flush_page_buffer();
                    s_meta.state = LOG_STATE_IDLE;
                    save_metadata();
                } else {
                    s_post_trigger_remaining -= LOG_ENTRY_SIZE;
                }
            }
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
    uint8_t state = s_state;
    if (state != LOG_STATE_RECORDING && state != LOG_STATE_ARMED &&
        state != LOG_STATE_CAPTURING)
        return;

    const gateway_frame_t *gf = (const gateway_frame_t *)gf_ptr;
    if (!passes_bus_filter((uint8_t)gf->source_bus)) return;

    if (xQueueSend(s_log_queue, gf, 0) != pdTRUE) {
        s_drop_count++;
    }
}

void flash_logger_start(void)
{
    if (s_state == LOG_STATE_RECORDING) return;
    if (s_state == LOG_STATE_ERROR) return;

    s_meta.start_timestamp = time_us_32() / 1000;
    s_meta.state = LOG_STATE_RECORDING;
    s_state = LOG_STATE_RECORDING;
    s_page_buf_pos = 0;
    s_drop_count = 0;

    save_metadata();
}

void flash_logger_stop(void)
{
    uint8_t state = s_state;
    if (state != LOG_STATE_RECORDING && state != LOG_STATE_ARMED &&
        state != LOG_STATE_CAPTURING)
        return;

    s_state = LOG_STATE_IDLE;
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
    s_drop_count = 0;
}

/* ---- Status Accessors ---- */

uint8_t  flash_logger_get_state(void)       { return s_state; }
uint8_t  flash_logger_get_mode(void)        { return s_meta.mode; }
void     flash_logger_set_mode(uint8_t m)   { if (m <= LOG_MODE_TRIGGERED) s_meta.mode = m; }
uint8_t  flash_logger_get_bus_mask(void)    { return s_meta.bus_mask; }
void     flash_logger_set_bus_mask(uint8_t m){ s_meta.bus_mask = m; }
uint32_t flash_logger_get_entry_count(void) { return s_meta.entry_count; }
uint32_t flash_logger_get_wrap_count(void)  { return s_meta.wrap_count; }
uint32_t flash_logger_get_write_offset(void){ return s_meta.write_offset; }
uint16_t flash_logger_get_flash_errors(void){ return s_meta.flash_errors; }
uint32_t flash_logger_get_drop_count(void)  { return s_drop_count; }

void flash_logger_arm(void)
{
    if (s_state != LOG_STATE_IDLE) return;
    if (s_meta.mode != LOG_MODE_TRIGGERED) return;

    s_meta.start_timestamp = time_us_32() / 1000;
    s_meta.state = LOG_STATE_ARMED;
    s_state = LOG_STATE_ARMED;
    s_page_buf_pos = 0;
    s_drop_count = 0;
    s_trigger_offset = 0;
    s_post_trigger_remaining = 0;

    save_metadata();
}

/* ---- Trigger Config Accessors ---- */

void     flash_logger_set_trigger_bus(uint8_t bus)    { s_meta.trigger_bus = bus; }
void     flash_logger_set_trigger_id(uint32_t id)     { s_meta.trigger_id = id; }
void     flash_logger_set_trigger_byte(uint8_t idx)   { if (idx < 8) s_meta.trigger_byte = idx; }
void     flash_logger_set_trigger_op(uint8_t op)      { if (op <= LOG_TRIGGER_OP_MASK) s_meta.trigger_op = op; }
void     flash_logger_set_trigger_value(uint8_t val)  { s_meta.trigger_value = val; }
void     flash_logger_set_pre_trigger_kb(uint16_t kb) { s_meta.pre_trigger_kb = kb; }
void     flash_logger_set_post_trigger_kb(uint16_t kb){ s_meta.post_trigger_kb = kb; }
uint8_t  flash_logger_get_trigger_bus(void)           { return s_meta.trigger_bus; }
uint32_t flash_logger_get_trigger_id(void)            { return s_meta.trigger_id; }
uint8_t  flash_logger_get_trigger_byte(void)          { return s_meta.trigger_byte; }
uint8_t  flash_logger_get_trigger_op(void)            { return s_meta.trigger_op; }
uint8_t  flash_logger_get_trigger_value(void)         { return s_meta.trigger_value; }
uint16_t flash_logger_get_pre_trigger_kb(void)        { return (uint16_t)s_meta.pre_trigger_kb; }
uint16_t flash_logger_get_post_trigger_kb(void)       { return (uint16_t)s_meta.post_trigger_kb; }

uint16_t flash_logger_read_chunk(uint32_t offset, uint8_t *buf, uint16_t len)
{
    uint32_t abs_addr = LOG_DATA_OFFSET + offset;
    if (abs_addr >= LOG_DATA_END) return 0;
    if (abs_addr + len > LOG_DATA_END)
        len = (uint16_t)(LOG_DATA_END - abs_addr);

    /* Read in small sub-chunks to limit interrupt-off time.
     * Each 256-byte read takes ~0.3ms with interrupts disabled.
     * CAN PIO IRQ runs between sub-chunks. */
    uint16_t remaining = len;
    uint16_t pos = 0;
    while (remaining > 0) {
        uint16_t chunk = (remaining > NVM_PAGE_SIZE) ? NVM_PAGE_SIZE : remaining;
        uint32_t irq = sec_flash_acquire_bus();
        sec_flash_read(abs_addr + pos, &buf[pos], chunk);
        sec_flash_release_bus(irq);
        pos += chunk;
        remaining -= chunk;
    }

    return len;
}
