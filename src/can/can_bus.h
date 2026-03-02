#ifndef CAN_BUS_H
#define CAN_BUS_H

#include <stdint.h>
#include <stdbool.h>
#include "hal/hal_gpio.h"

/* ---- CAN Frame ---- */
typedef struct {
    uint32_t id;            /* 11-bit or 29-bit arbitration ID */
    uint8_t  dlc;           /* Data length code (0-8) */
    uint8_t  flags;         /* CAN_FLAG_* bits */
    uint8_t  data[8];       /* Payload */
} can_frame_t;

/* CAN frame flags */
#define CAN_FLAG_RTR    (1 << 0)    /* Remote Transmission Request */
#define CAN_FLAG_EFF    (1 << 1)    /* Extended Frame Format (29-bit) */

/* ---- Bus State ---- */
typedef enum {
    CAN_STATE_UNINIT = 0,
    CAN_STATE_ACTIVE,
    CAN_STATE_ERROR,
    CAN_STATE_BUS_OFF,
    CAN_STATE_DISABLED,
    CAN_STATE_TIMEOUT,
} can_bus_state_t;

/* ---- Bus Statistics ---- */
typedef struct {
    uint32_t rx_count;
    uint32_t tx_count;
    uint32_t error_count;
    uint32_t ring_overflow_count;
    can_bus_state_t state;
} can_bus_stats_t;

/* ---- Gateway Frame (bus-tagged, for inter-task queues) ---- */
typedef enum {
    BUS_CAN1 = 0,
    BUS_CAN2 = 1,
    BUS_LIN1 = 2,
    BUS_LIN2 = 3,
    BUS_LIN3 = 4,
    BUS_LIN4 = 5,
    BUS_COUNT
} bus_id_t;

typedef struct {
    bus_id_t   source_bus;
    can_frame_t frame;      /* CAN frame payload (also used for LIN→CAN mapping) */
    uint32_t    timestamp;  /* System tick at reception */
} gateway_frame_t;

#endif /* CAN_BUS_H */
