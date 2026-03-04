#ifndef DIAGNOSTICS_H
#define DIAGNOSTICS_H

#include <stdint.h>
#include "board_config.h"
#include "FreeRTOS.h"
#include "task.h"

/**
 * Initialize the diagnostics module.
 * Must be called before creating the diagnostics task.
 *
 * @param task_handles  Array of task handles for stack watermark monitoring
 * @param num_tasks     Number of entries in task_handles
 */
void diagnostics_init(TaskHandle_t *task_handles, uint8_t num_tasks);

/**
 * Set the reset reason (call once from main before scheduler starts).
 */
void diagnostics_set_reset_reason(reset_reason_t reason);

/**
 * FreeRTOS task entry point — replaces the old inline diag_task in main.c.
 * Sends heartbeat frames on the configured CAN bus at the configured interval.
 */
void diagnostics_task(void *params);

/**
 * Get the current system state.
 */
system_state_t diagnostics_get_state(void);

/**
 * Reconfigure diagnostics from the NVM config.
 * Called from apply_config() when settings change at runtime.
 */
void diagnostics_reconfigure(void);

#endif /* DIAGNOSTICS_H */
