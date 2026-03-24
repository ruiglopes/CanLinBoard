#ifndef CONFIG_HANDLER_H
#define CONFIG_HANDLER_H

#include <stdint.h>
#include <stdbool.h>
#include "config/nvm_config.h"
#include "FreeRTOS.h"
#include "queue.h"
#include "semphr.h"

/**
 * Initialize the config handler.
 * Loads config from NVM (or defaults if no valid config found).
 */
void config_handler_init(QueueHandle_t config_rx_queue, QueueHandle_t can_tx_queue);

/**
 * Config task main loop — call from FreeRTOS task. Never returns.
 */
void config_handler_task(void *params);

/**
 * Get pointer to the current working config (read-only).
 * Valid after config_handler_init().
 */
const nvm_config_t *config_handler_get_config(void);

/**
 * Lock the config mutex. Must be held while reading multi-byte fields
 * from the config returned by config_handler_get_config().
 * Copy fields to locals and unlock immediately — do NOT hold across
 * vTaskDelay() or any blocking call. Non-recursive.
 */
void config_handler_lock(void);
void config_handler_unlock(void);

#endif /* CONFIG_HANDLER_H */
