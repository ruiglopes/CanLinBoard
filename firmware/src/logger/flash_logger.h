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
    uint32_t trigger_id;      /* CAN/LIN ID to match */
    uint8_t  trigger_bus;     /* bus to watch (0-5) */
    uint8_t  trigger_byte;    /* data byte index to compare (0-7) */
    uint8_t  trigger_op;      /* 0=any, 1=equals, 2=gt, 3=lt, 4=mask */
    uint8_t  trigger_value;   /* comparison value */
    uint32_t pre_trigger_kb;  /* KB of pre-trigger data to retain */
    uint32_t post_trigger_kb; /* KB of post-trigger data to capture */
    uint32_t crc32;           /* metadata integrity check */
} log_metadata_t;

_Static_assert(sizeof(log_metadata_t) <= 256, "log_metadata_t must fit in one page");

/* ---- Logger States ---- */

#define LOG_STATE_IDLE          0
#define LOG_STATE_RECORDING     1
#define LOG_STATE_ARMED         2
#define LOG_STATE_CAPTURING     3
#define LOG_STATE_ERROR         4

/* ---- Logger Modes ---- */

#define LOG_MODE_MANUAL         0
#define LOG_MODE_CONTINUOUS     1
#define LOG_MODE_TRIGGERED      2

/* ---- Trigger Operators ---- */
#define LOG_TRIGGER_OP_ANY      0   /* any frame on trigger_bus with trigger_id */
#define LOG_TRIGGER_OP_EQ       1   /* data[trigger_byte] == trigger_value */
#define LOG_TRIGGER_OP_GT       2   /* data[trigger_byte] > trigger_value */
#define LOG_TRIGGER_OP_LT       3   /* data[trigger_byte] < trigger_value */
#define LOG_TRIGGER_OP_MASK     4   /* (data[trigger_byte] & trigger_value) != 0 */

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
#define LOG_PARAM_DROP_COUNT    8   /* R, 4 bytes (sub=0/1 split) — frames dropped due to full queue */
#define LOG_PARAM_TRIGGER_BUS   9   /* R/W, 1 byte */
#define LOG_PARAM_TRIGGER_ID    10  /* R/W, 4 bytes (sub=0/1 split) */
#define LOG_PARAM_TRIGGER_BYTE  11  /* R/W, 1 byte */
#define LOG_PARAM_TRIGGER_OP    12  /* R/W, 1 byte */
#define LOG_PARAM_TRIGGER_VALUE 13  /* R/W, 1 byte */
#define LOG_PARAM_PRE_TRIG_KB   14  /* R/W, 2 bytes */
#define LOG_PARAM_POST_TRIG_KB  15  /* R/W, 2 bytes */

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
void flash_logger_arm(void);

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
uint32_t flash_logger_get_drop_count(void);

/* ---- Trigger Config Accessors ---- */

void     flash_logger_set_trigger_bus(uint8_t bus);
void     flash_logger_set_trigger_id(uint32_t id);
void     flash_logger_set_trigger_byte(uint8_t idx);
void     flash_logger_set_trigger_op(uint8_t op);
void     flash_logger_set_trigger_value(uint8_t val);
void     flash_logger_set_pre_trigger_kb(uint16_t kb);
void     flash_logger_set_post_trigger_kb(uint16_t kb);
uint8_t  flash_logger_get_trigger_bus(void);
uint32_t flash_logger_get_trigger_id(void);
uint8_t  flash_logger_get_trigger_byte(void);
uint8_t  flash_logger_get_trigger_op(void);
uint8_t  flash_logger_get_trigger_value(void);
uint16_t flash_logger_get_pre_trigger_kb(void);
uint16_t flash_logger_get_post_trigger_kb(void);

/**
 * Read a chunk of log data from flash for download.
 * @param offset  Byte offset into the ring buffer (relative to LOG_DATA_OFFSET)
 * @param buf     Output buffer
 * @param len     Bytes to read (max 4096)
 * @return Actual bytes read
 */
uint16_t flash_logger_read_chunk(uint32_t offset, uint8_t *buf, uint16_t len);

#endif /* FLASH_LOGGER_H */
