#ifndef LIN_MANAGER_H
#define LIN_MANAGER_H

#include <stdint.h>
#include <stdbool.h>
#include "lin/lin_bus.h"
#include "lin/sja1124_driver.h"
#include "can/can_bus.h"
#include "FreeRTOS.h"
#include "queue.h"

/**
 * Initialize the LIN manager.
 * Sets up SJA1124, GPIO interrupt for INTN.
 * @param gateway_queue  Queue for forwarding LIN RX frames to gateway
 * @param lin_tx_queue   Queue for outbound LIN frames (from gateway)
 */
void lin_manager_init(QueueHandle_t gateway_queue, QueueHandle_t lin_tx_queue);

/**
 * Configure and start a LIN channel.
 */
bool lin_manager_start_channel(uint8_t ch, const lin_channel_config_t *config);

/**
 * Stop a LIN channel.
 */
void lin_manager_stop_channel(uint8_t ch);

/**
 * Set the schedule table for a master channel.
 */
bool lin_manager_set_schedule(uint8_t ch, const lin_schedule_table_t *table);

/**
 * Transmit a LIN frame on a specific channel.
 * Non-blocking, queued for processing by lin_task.
 */
bool lin_manager_transmit(uint8_t ch, const lin_frame_t *frame);

/**
 * Get statistics for a LIN channel.
 */
void lin_manager_get_stats(uint8_t ch, lin_channel_stats_t *stats);

/**
 * Main LIN task function (FreeRTOS task entry point).
 * Handles SJA1124 interrupts, schedule ticks, TX queue.
 */
void lin_task_entry(void *params);

#endif /* LIN_MANAGER_H */
