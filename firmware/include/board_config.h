#ifndef BOARD_CONFIG_H
#define BOARD_CONFIG_H

#include <stdint.h>

/* ---- Firmware Version ---- */
#define FW_VERSION_MAJOR    0
#define FW_VERSION_MINOR    1
#define FW_VERSION_PATCH    0
#define FW_VERSION_PACKED   ((FW_VERSION_MAJOR << 16) | (FW_VERSION_MINOR << 8) | FW_VERSION_PATCH)

/* ---- System Clock ---- */
#define SYS_CLOCK_HZ       150000000U  /* RP2350 default 150 MHz */

/* ---- CAN1 Transceiver Pins ---- */
#define CAN1_RX_PIN         1
#define CAN1_TX_PIN         2
#define CAN1_EN_PIN         3   /* Active LOW to enable transceiver */
#define CAN1_TERM_PIN       4   /* HIGH to enable termination */

/* ---- CAN2 Transceiver Pins ---- */
#define CAN2_RX_PIN         13
#define CAN2_TX_PIN         14
#define CAN2_EN_PIN         15  /* Active LOW to enable transceiver */
#define CAN2_TERM_PIN       12  /* HIGH to enable termination */

/* ---- CAN PIO Assignment ---- */
#define CAN1_PIO_NUM        0   /* PIO0 */
#define CAN2_PIO_NUM        1   /* PIO1 */

/* ---- CAN Defaults ---- */
#define CAN_DEFAULT_BITRATE 500000U

/* ---- LIN Transceiver (SJA1124) SPI Pins ---- */
#define LIN_SPI_PORT        spi0
#define LIN_SPI_INST        0       /* SPI instance number */
#define LIN_CS_PIN          33
#define LIN_SCK_PIN         34
#define LIN_MISO_PIN        32
#define LIN_MOSI_PIN        23
#define LIN_STAT_PIN        28
#define LIN_INT_PIN         26      /* INTN - active LOW interrupt */
#define LIN_CLOCK_PIN       21      /* Clock output for SJA1124 PLL */

/* ---- LIN SPI Configuration ---- */
#define LIN_SPI_BAUDRATE    4000000U    /* 4 MHz max for SJA1124 */
#define LIN_SPI_CPOL        0
#define LIN_SPI_CPHA        1

/* ---- LIN Defaults ---- */
#define LIN_DEFAULT_BAUDRATE    19200U
#define LIN_CHANNEL_COUNT       4
#define LIN_CLOCK_FREQ_HZ       8000000U    /* 8 MHz output for SJA1124 PLL */

/* ---- Flash Memory Map ---- */
#define FLASH_BASE              0x10000000U
#define BL_CODE_SIZE            (28U * 1024U)
#define BL_CONFIG_SIZE          (4U * 1024U)
#define BL_TOTAL_SIZE           (BL_CODE_SIZE + BL_CONFIG_SIZE)  /* 32 KB */
#define APP_BASE                (FLASH_BASE + BL_TOTAL_SIZE)     /* 0x10008000 */
#define APP_HEADER_SIZE         256U
#define APP_CODE_BASE           (APP_BASE + APP_HEADER_SIZE)     /* 0x10008100 */

/* ---- NVM Storage (tail of primary flash) ---- */
/*
 * Uses the last 12 KB of the 2 MB primary flash (CS0).
 * CS1 secondary flash requires QMI init not yet implemented.
 *
 *   0x101FD000  Slot A  (4 KB)
 *   0x101FE000  Slot B  (4 KB)
 *   0x101FF000  Meta    (4 KB)
 *   0x10200000  End of 2 MB flash
 */
#define PRIMARY_FLASH_SIZE      (2U * 1024U * 1024U)  /* 2 MB */
#define NVM_SECTOR_SIZE         4096U
#define NVM_PAGE_SIZE           256U
#define NVM_SECTOR_COUNT        3U  /* Slot A + Slot B + Meta */
#define NVM_FLASH_OFFSET        (PRIMARY_FLASH_SIZE - (NVM_SECTOR_COUNT * NVM_SECTOR_SIZE))  /* 0x1FD000 */
#define NVM_SLOT_A_OFFSET       0U
#define NVM_SLOT_B_OFFSET       NVM_SECTOR_SIZE
#define NVM_META_OFFSET         (2U * NVM_SECTOR_SIZE)

/* ---- Bootloader Interop ---- */
#define SRAM_MAGIC_ADDR         (0x20000000U + (512U * 1024U) - 4U)  /* 0x2007FFFC */
#define SRAM_MAGIC_VALUE        0xB00710ADU
#define CMD_RESET               0x05U
#define RESET_MODE_BOOTLOADER   0x01U
#define RESET_UNLOCK_KEY        0xB007CAFEU

/* ---- CAN Configuration Protocol IDs ---- */
#define CONFIG_CAN_CMD_ID       0x600U
#define CONFIG_CAN_RESP_ID      0x601U
#define CONFIG_CAN_DATA_ID      0x602U

/* ---- Bootloader CAN IDs ---- */
#define BL_CAN_CMD_ID           0x700U
#define BL_CAN_RESP_ID          0x701U
#define BL_CAN_DATA_ID          0x702U
#define BL_CAN_DEBUG_ID         0x7FFU

/* ---- FreeRTOS Task Priorities ---- */
#define TASK_PRIORITY_CAN       5
#define TASK_PRIORITY_LIN       4
#define TASK_PRIORITY_GATEWAY   3
#define TASK_PRIORITY_CONFIG    2
#define TASK_PRIORITY_DIAG      1

/* ---- FreeRTOS Task Stack Sizes (words) ---- */
#define TASK_STACK_CAN          512
#define TASK_STACK_LIN          512
#define TASK_STACK_GATEWAY      1024
#define TASK_STACK_CONFIG       768
#define TASK_STACK_DIAG         384

/* ---- Queue Depths ---- */
#define QUEUE_DEPTH_GATEWAY_IN  32
#define QUEUE_DEPTH_CAN_TX      16
#define QUEUE_DEPTH_LIN_TX      16
#define QUEUE_DEPTH_CONFIG_RX   8

/* ---- Ring Buffer Sizes (must be power of 2) ---- */
#define CAN_RX_RING_SIZE        32

/* ---- Gateway Limits ---- */
#define MAX_ROUTING_RULES       32
#define MAX_BYTE_MAPPINGS       8
#define MAX_SCHEDULE_ENTRIES    16

/* ---- NVM Configuration ---- */
#define NVM_CONFIG_MAGIC        0x4E564D01U  /* "NVM\x01" */
#define NVM_CONFIG_VERSION      1
#define NVM_META_MAGIC          0x4E564D4DU  /* "NVMM" */

/* ---- Diagnostics ---- */
#define DIAG_DEFAULT_CAN_ID     0x7F0U
#define DIAG_STATS_CAN_ID      0x7F1U
#define DIAG_LIN_STATS_CAN_ID  0x7F2U
#define DIAG_CRASH_CAN_ID      0x7F3U
#define DIAG_DEFAULT_INTERVAL_MS 100U
#define HW_WATCHDOG_TIMEOUT_MS  5000U

/* ---- System State ---- */
typedef enum {
    SYS_STATE_BOOT = 0,
    SYS_STATE_OK,
    SYS_STATE_WARN,
    SYS_STATE_ERROR,
} system_state_t;

/* ---- Reset Reason ---- */
typedef enum {
    RESET_POWER_ON = 0,
    RESET_WATCHDOG_TIMEOUT,
    RESET_CRASH_REBOOT,
    RESET_UNKNOWN,
} reset_reason_t;

#endif /* BOARD_CONFIG_H */
