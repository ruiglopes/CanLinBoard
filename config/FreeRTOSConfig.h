#ifndef FREERTOS_CONFIG_H
#define FREERTOS_CONFIG_H

/* ---- Pico SDK integration ---- */
/* Use the RP2040/RP2350 port provided by pico-sdk */
#include "rp2040_config.h"

/* ---- Core Configuration ---- */
#define configUSE_PREEMPTION                    1
#define configUSE_PORT_OPTIMISED_TASK_SELECTION 0
#define configUSE_TICKLESS_IDLE                 0
#define configCPU_CLOCK_HZ                      150000000U
#define configTICK_RATE_HZ                      1000U
#define configMAX_PRIORITIES                    6
#define configMINIMAL_STACK_SIZE                128
#define configMAX_TASK_NAME_LEN                 8
#define configUSE_16_BIT_TICKS                  0
#define configIDLE_SHOULD_YIELD                 1
#define configTASK_NOTIFICATION_ARRAY_ENTRIES   2

/* ---- Memory ---- */
#define configTOTAL_HEAP_SIZE                   (48 * 1024)
#define configSUPPORT_STATIC_ALLOCATION         0
#define configSUPPORT_DYNAMIC_ALLOCATION        1
#define configAPPLICATION_ALLOCATED_HEAP        0

/* ---- Queue / Semaphore / Mutex ---- */
#define configUSE_MUTEXES                       1
#define configUSE_RECURSIVE_MUTEXES             0
#define configUSE_COUNTING_SEMAPHORES           1
#define configQUEUE_REGISTRY_SIZE               8
#define configUSE_QUEUE_SETS                    0

/* ---- Timers ---- */
#define configUSE_TIMERS                        1
#define configTIMER_TASK_PRIORITY               2
#define configTIMER_QUEUE_LENGTH                10
#define configTIMER_TASK_STACK_DEPTH            256

/* ---- Hook Functions ---- */
#define configUSE_IDLE_HOOK                     1
#define configUSE_TICK_HOOK                     0
#define configUSE_MALLOC_FAILED_HOOK            1
#define configCHECK_FOR_STACK_OVERFLOW          2

/* ---- Co-routines (unused) ---- */
#define configUSE_CO_ROUTINES                   0

/* ---- Interrupt Nesting ----
 * ARM Cortex-M33 on RP2350: 4 priority bits (0-15), lower = higher priority.
 * can2040 IRQ runs at priority 1 and calls FromISR functions.
 * configMAX_SYSCALL_INTERRUPT_PRIORITY must allow priority 1.
 */
#define configPRIO_BITS                         4
#define configLIBRARY_LOWEST_INTERRUPT_PRIORITY         15
#define configLIBRARY_MAX_SYSCALL_INTERRUPT_PRIORITY    1
#define configKERNEL_INTERRUPT_PRIORITY          (configLIBRARY_LOWEST_INTERRUPT_PRIORITY << (8 - configPRIO_BITS))
#define configMAX_SYSCALL_INTERRUPT_PRIORITY     (configLIBRARY_MAX_SYSCALL_INTERRUPT_PRIORITY << (8 - configPRIO_BITS))

/* ---- INCLUDE functions ---- */
#define INCLUDE_vTaskPrioritySet                1
#define INCLUDE_uxTaskPriorityGet               1
#define INCLUDE_vTaskDelete                     1
#define INCLUDE_vTaskSuspend                    1
#define INCLUDE_vTaskDelayUntil                 1
#define INCLUDE_vTaskDelay                      1
#define INCLUDE_xTaskGetSchedulerState          1
#define INCLUDE_xTaskGetCurrentTaskHandle       1
#define INCLUDE_xTimerPendFunctionCall          1
#define INCLUDE_xTaskAbortDelay                 0
#define INCLUDE_xTaskGetHandle                  0

/* ---- SMP Configuration (single core for now) ---- */
#define configNUMBER_OF_CORES                   1
#define configRUN_MULTIPLE_PRIORITIES            0

#endif /* FREERTOS_CONFIG_H */
