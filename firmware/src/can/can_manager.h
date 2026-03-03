#ifndef CAN_MANAGER_H
#define CAN_MANAGER_H

#include <stdint.h>
#include <stdbool.h>
#include "can/can_bus.h"
#include "FreeRTOS.h"
#include "queue.h"

/**
 * Initialize the CAN manager.
 * Sets up both can2040 instances, ring buffers, and IRQ handlers.
 * Must be called after FreeRTOS scheduler is running (needs queue handles).
 *
 * @param gateway_queue  Queue for gateway-bound frames
 * @param config_queue   Queue for config-protocol frames (CAN1 only)
 * @param can_tx_queue   Queue for outbound CAN frames (from gateway/config)
 */
void can_manager_init(QueueHandle_t gateway_queue,
                      QueueHandle_t config_queue,
                      QueueHandle_t can_tx_queue);

/**
 * Start CAN1 at the given bitrate. Enables the transceiver.
 */
bool can_manager_start_can1(uint32_t bitrate);

/**
 * Start CAN2 at the given bitrate. Enables the transceiver.
 */
bool can_manager_start_can2(uint32_t bitrate);

/**
 * Stop CAN2 (disable transceiver, stop can2040 instance).
 */
void can_manager_stop_can2(void);

/**
 * Transmit a CAN frame on the specified bus.
 * Non-blocking. Returns false if TX queue is full.
 */
bool can_manager_transmit(can_bus_id_t bus, const can_frame_t *frame);

/**
 * Get statistics for a CAN bus.
 */
void can_manager_get_stats(can_bus_id_t bus, can_bus_stats_t *stats);

/**
 * Main CAN task function. Drains ring buffers, handles TX queue.
 * To be called as a FreeRTOS task entry point.
 */
void can_task_entry(void *params);

#endif /* CAN_MANAGER_H */
