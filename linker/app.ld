/*
 * Linker script for CAN/LIN Gateway application.
 * Boots via 2350Bootloader — no .boot2 section needed.
 * App header at 0x10008000 (256 bytes), code starts at 0x10008100.
 */

MEMORY
{
    APP_HDR(rx)   : ORIGIN = 0x10008000, LENGTH = 256
    FLASH(rx)     : ORIGIN = 0x10008100, LENGTH = 2M - 0x8100
    RAM(rwx)      : ORIGIN = 0x20000000, LENGTH = 512k
    SCRATCH_X(rwx): ORIGIN = 0x20080000, LENGTH = 4k
    SCRATCH_Y(rwx): ORIGIN = 0x20081000, LENGTH = 4k
}

ENTRY(_entry_point)

SECTIONS
{
    /* Application header — exactly 256 bytes at 0x10008000 */
    .app_header : {
        KEEP(*(.app_header))
    } > APP_HDR

    /* Vector table at start of FLASH (0x10008100) — this is VTOR */
    .vectors : {
        __vectors_start = .;
        KEEP(*(.vectors))
        KEEP(*(.vectors.*))
    } > FLASH

    /* Executable code */
    .text : {
        __text_start = .;
        *(.text)
        *(.text.*)
        *(.gnu.linkonce.t.*)
        . = ALIGN(4);
        __text_end = .;
    } > FLASH

    /* ARM exception/unwind tables */
    .ARM.extab : {
        *(.ARM.extab* .gnu.linkonce.armextab.*)
    } > FLASH

    .ARM.exidx : {
        __exidx_start = .;
        *(.ARM.exidx* .gnu.linkonce.armexidx.*)
        __exidx_end = .;
    } > FLASH

    /* Read-only data */
    .rodata : {
        *(.rodata)
        *(.rodata.*)
        *(.gnu.linkonce.r.*)
        . = ALIGN(4);
    } > FLASH

    /* Pico SDK mutex array */
    .mutex_array : {
        __mutex_array_start = .;
        KEEP(*(SORT(.mutex_array.*)))
        KEEP(*(.mutex_array))
        __mutex_array_end = .;
    } > FLASH

    /* Pico SDK binary info */
    .binary_info : {
        __binary_info_start = .;
        KEEP(*(.binary_info.keep.*))
        __binary_info_end = .;
    } > FLASH

    /* Initializers */
    .preinit_array : {
        __preinit_array_start = .;
        KEEP(*(.preinit_array*))
        __preinit_array_end = .;
    } > FLASH

    .init_array : {
        __init_array_start = .;
        KEEP(*(SORT(.init_array.*)))
        KEEP(*(.init_array*))
        __init_array_end = .;
    } > FLASH

    /* End of flash content */
    __etext = .;

    /* Data section (initialized, copied from FLASH to RAM) */
    .data : {
        __data_start__ = .;
        *(.time_critical*)
        *(.data)
        *(.data.*)
        *(.gnu.linkonce.d.*)
        *(.after_data.*)
        . = ALIGN(4);
        __data_end__ = .;
    } > RAM AT> FLASH

    /* BSS section (zero-initialized) */
    .bss (NOLOAD) : {
        __bss_start__ = .;
        *(.bss)
        *(.bss.*)
        *(.gnu.linkonce.b.*)
        *(COMMON)
        . = ALIGN(4);
        __bss_end__ = .;
    } > RAM

    /* Heap and stack in remaining RAM */
    .heap (NOLOAD) : {
        __end__ = .;
        end = .;
        __heap_start = .;
        . = . + 0;  /* Heap grows up, managed by FreeRTOS */
        __heap_end = .;
        __HeapLimit = .;
    } > RAM

    /* Stack at end of main RAM */
    __StackTop = ORIGIN(RAM) + LENGTH(RAM);
    __StackLimit = __StackTop - 4096;
    __stack = __StackTop;

    /* Scratch memory */
    .scratch_x : {
        __scratch_x_start__ = .;
        *(.scratch_x.*)
        __scratch_x_end__ = .;
    } > SCRATCH_X AT> FLASH
    __scratch_x_source__ = LOADADDR(.scratch_x);

    .scratch_y : {
        __scratch_y_start__ = .;
        *(.scratch_y.*)
        __scratch_y_end__ = .;
    } > SCRATCH_Y AT> FLASH
    __scratch_y_source__ = LOADADDR(.scratch_y);

    /* End of binary — used by patch_header.py to compute size */
    __binary_end = LOADADDR(.scratch_y) + SIZEOF(.scratch_y);
    __app_binary_start = ORIGIN(FLASH);

    /* Discard debug sections to save space */
    /DISCARD/ : {
        *(.comment)
        *(.note.GNU-stack)
    }
}
