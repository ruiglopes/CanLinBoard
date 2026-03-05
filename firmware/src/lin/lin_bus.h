#ifndef LIN_BUS_H
#define LIN_BUS_H

#include <stdint.h>
#include <stdbool.h>
#include "board_config.h"

/* ---- LIN Frame ---- */
typedef struct {
    uint8_t  id;            /* 6-bit LIN frame ID (0-63, no parity) */
    uint8_t  dlc;           /* Data length (1-8) */
    uint8_t  data[8];       /* Payload */
    bool     classic_cs;    /* true = classic checksum, false = enhanced (LIN 2.x) */
} lin_frame_t;

/* ---- LIN Channel Mode ---- */
typedef enum {
    LIN_MODE_DISABLED = 0,
    LIN_MODE_MASTER,
    LIN_MODE_SLAVE,
} lin_mode_t;

/* ---- LIN Channel State ---- */
typedef enum {
    LIN_STATE_UNINIT = 0,
    LIN_STATE_INIT,
    LIN_STATE_IDLE,
    LIN_STATE_ACTIVE,
    LIN_STATE_ERROR,
    LIN_STATE_TIMEOUT,
} lin_state_t;

/* ---- LIN Schedule Entry ---- */
typedef struct {
    uint8_t  id;            /* LIN frame ID */
    uint8_t  dlc;           /* Data length */
    uint8_t  dir;           /* 0=receive (subscriber), 1=transmit (publisher) */
    uint8_t  data[8];       /* Data to transmit (publisher mode) */
    uint16_t delay_ms;      /* Delay before this entry (in ms) */
    bool     classic_cs;    /* Checksum type */
} lin_schedule_entry_t;

/* ---- LIN Schedule Table ---- */
typedef struct {
    uint8_t              count;                         /* Number of entries (0 = no schedule) */
    lin_schedule_entry_t entries[MAX_SCHEDULE_ENTRIES];  /* Schedule entries */
} lin_schedule_table_t;

_Static_assert(sizeof(lin_schedule_entry_t) == 16, "lin_schedule_entry_t size changed");
_Static_assert(sizeof(lin_schedule_table_t) == 258, "lin_schedule_table_t size changed");

/* ---- LIN Channel Configuration ---- */
typedef struct {
    bool        enabled;
    lin_mode_t  mode;
    uint32_t    baudrate;
    lin_schedule_table_t schedule;
} lin_channel_config_t;

/* ---- LIN Channel Statistics ---- */
typedef struct {
    uint32_t    rx_count;
    uint32_t    tx_count;
    uint32_t    error_count;
    uint32_t    timeout_count;
    lin_state_t state;
} lin_channel_stats_t;

#endif /* LIN_BUS_H */
