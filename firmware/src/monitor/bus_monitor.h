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
