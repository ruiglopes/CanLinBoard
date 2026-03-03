#ifndef FAULT_HANDLER_H
#define FAULT_HANDLER_H

#include <stdint.h>
#include <stdbool.h>

/* ---- Fault Type ---- */
typedef enum {
    FAULT_NONE = 0,
    FAULT_HARDFAULT,
    FAULT_STACK_OVERFLOW,
    FAULT_MALLOC_FAIL,
    FAULT_ASSERT_FAIL,
    FAULT_WATCHDOG_TIMEOUT,
} fault_type_t;

/* ---- Crash Data (64 bytes, placed in NOLOAD SRAM — survives warm reboot) ---- */
#define CRASH_DATA_MAGIC    0xDEADFA17U

typedef struct {
    uint32_t    magic;          /* CRASH_DATA_MAGIC if valid */
    uint32_t    fault_type;     /* fault_type_t */
    char        task_name[8];   /* Name of faulting task (or "???" if unknown) */
    uint32_t    pc;             /* Program counter at fault */
    uint32_t    lr;             /* Link register at fault */
    uint32_t    psp;            /* Process stack pointer */
    uint32_t    msp;            /* Main stack pointer */
    uint32_t    cfsr;           /* Configurable Fault Status Register */
    uint32_t    hfsr;           /* HardFault Status Register */
    uint32_t    mmfar;          /* MemManage Fault Address */
    uint32_t    bfar;           /* BusFault Address */
    uint32_t    uptime_ms;      /* Uptime at crash (from hw timer) */
    uint32_t    reserved[2];    /* Pad to 64 bytes */
} crash_data_t;

/**
 * Initialize the fault handler.
 * Checks for stale crash data and sets up the HardFault vector.
 */
void fault_handler_init(void);

/**
 * Get pointer to crash data. Check magic == CRASH_DATA_MAGIC for validity.
 */
const crash_data_t *fault_handler_get_crash_data(void);

/**
 * Clear crash data (after it has been reported).
 */
void fault_handler_clear(void);

/**
 * Save crash data for stack overflow (called from vApplicationStackOverflowHook).
 * Reboots the MCU after saving. Does not return.
 */
void fault_handler_save_stack_overflow(const char *task_name) __attribute__((noreturn));

/**
 * Save crash data for malloc failure (called from vApplicationMallocFailedHook).
 * Reboots the MCU after saving. Does not return.
 */
void fault_handler_save_malloc_fail(void) __attribute__((noreturn));

/**
 * Save crash data for configASSERT failure.
 * Reboots the MCU after saving. Does not return.
 * @param pc  Return address from __builtin_return_address(0)
 * @param lr  Return address from __builtin_return_address(1) or 0
 */
void fault_handler_save_assert(void *pc, void *lr) __attribute__((noreturn));

#endif /* FAULT_HANDLER_H */
