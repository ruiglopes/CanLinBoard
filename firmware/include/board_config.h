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

/* ---- Secondary Flash (NVM) ---- */
#define NVM_FLASH_BASE          0x11000000U  /* XIP CS1 base (placeholder) */
#define NVM_SECTOR_SIZE         4096U
#define NVM_PAGE_SIZE           256U
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
#define TASK_STACK_CONFIG       512
#define TASK_STACK_DIAG         256

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

/* ---- Diagnostics ---- */
#define DIAG_DEFAULT_CAN_ID     0x7F0U
#define DIAG_DEFAULT_INTERVAL_MS 100U
#define HW_WATCHDOG_TIMEOUT_MS  5000U

#endif /* BOARD_CONFIG_H */
