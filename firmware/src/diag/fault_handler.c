#include "diag/fault_handler.h"
#include "hardware/watchdog.h"
#include "hardware/structs/watchdog.h"
#include <string.h>

/* ---- Watchdog scratch registers for crash data ----
 * scratch[0-3] are unused by the Pico SDK (scratch[4-7] are used by
 * watchdog_reboot/watchdog_enable). They survive all warm reboots
 * including through the bootloader, unlike SRAM which gets zeroed by
 * the bootloader's CRT0 startup.
 *
 *   scratch[0] = CRASH_DATA_MAGIC (valid marker)
 *   scratch[1] = fault_type
 *   scratch[2] = PC at crash
 *   scratch[3] = LR at crash
 */

/* ---- Crash data buffer (populated from scratch registers on boot) ---- */
static crash_data_t s_crash_data;

/* ---- Internal Helpers ---- */

__attribute__((noreturn))
static void save_and_reboot(fault_type_t type, uint32_t pc, uint32_t lr)
{
    /* Save critical fields to watchdog scratch registers (survive bootloader) */
    watchdog_hw->scratch[0] = CRASH_DATA_MAGIC;
    watchdog_hw->scratch[1] = (uint32_t)type;
    watchdog_hw->scratch[2] = pc;
    watchdog_hw->scratch[3] = lr;

    watchdog_reboot(0, 0, 0);
    for (;;) { __breakpoint(); }
}

/* ---- HardFault Handler ---- */

/**
 * C part of the HardFault handler. Called from the naked ASM trampoline
 * with the correct stack frame pointer.
 */
void hardfault_handler_c(uint32_t *stack_frame)
{
    /* Cortex-M exception stack frame layout:
     * [0]=R0, [1]=R1, [2]=R2, [3]=R3, [4]=R12, [5]=LR, [6]=PC, [7]=xPSR */
    uint32_t pc = stack_frame[6];
    uint32_t lr = stack_frame[5];

    /* Save critical fields to watchdog scratch registers (survive bootloader) */
    watchdog_hw->scratch[0] = CRASH_DATA_MAGIC;
    watchdog_hw->scratch[1] = (uint32_t)FAULT_HARDFAULT;
    watchdog_hw->scratch[2] = pc;
    watchdog_hw->scratch[3] = lr;

    watchdog_reboot(0, 0, 0);
    for (;;) { __breakpoint(); }
}

/**
 * Naked HardFault handler — determines which stack pointer was in use
 * (PSP or MSP) by testing EXC_RETURN bit 2, then calls the C handler.
 */
void __attribute__((naked)) isr_hardfault(void)
{
    __asm volatile(
        "tst lr, #4            \n"  /* Test EXC_RETURN bit 2 */
        "ite eq                \n"
        "mrseq r0, msp         \n"  /* If 0: exception used MSP */
        "mrsne r0, psp         \n"  /* If 1: exception used PSP */
        "b hardfault_handler_c \n"  /* Call C handler with stack frame in r0 */
    );
}

/* ---- Public API ---- */

void fault_handler_init(void)
{
    if (watchdog_hw->scratch[0] == CRASH_DATA_MAGIC) {
        /* Reconstruct crash data from watchdog scratch registers */
        memset(&s_crash_data, 0, sizeof(s_crash_data));
        s_crash_data.magic      = CRASH_DATA_MAGIC;
        s_crash_data.fault_type = watchdog_hw->scratch[1];
        s_crash_data.pc         = watchdog_hw->scratch[2];
        s_crash_data.lr         = watchdog_hw->scratch[3];
    } else {
        memset(&s_crash_data, 0, sizeof(s_crash_data));
    }
}

const crash_data_t *fault_handler_get_crash_data(void)
{
    return &s_crash_data;
}

void fault_handler_clear(void)
{
    memset(&s_crash_data, 0, sizeof(s_crash_data));
    watchdog_hw->scratch[0] = 0;  /* Clear scratch magic */
}

void fault_handler_save_stack_overflow(const char *task_name)
{
    (void)task_name;
    save_and_reboot(FAULT_STACK_OVERFLOW, 0, 0);
}

void fault_handler_save_malloc_fail(void)
{
    save_and_reboot(FAULT_MALLOC_FAIL, 0, 0);
}

void fault_handler_save_assert(void *pc, void *lr)
{
    save_and_reboot(FAULT_ASSERT_FAIL,
                    (uint32_t)(uintptr_t)pc, (uint32_t)(uintptr_t)lr);
}
