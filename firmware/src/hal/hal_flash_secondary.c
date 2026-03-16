#include "hal/hal_flash_secondary.h"
#include "board_config.h"
#include "pico/stdlib.h"
#include "hardware/flash.h"
#include "hardware/sync.h"
#include "hardware/gpio.h"
#include "hardware/xip_cache.h"
#include "hardware/structs/qmi.h"
#include "hardware/structs/io_bank0.h"
#include "hardware/regs/qmi.h"
#include "pico/bootrom.h"

/* ====================================================================
 * Boot2 copyout — needed to re-enter XIP after direct-mode access.
 * Mirrors the SDK's flash.c pattern.
 * ==================================================================== */

#define BOOT2_SIZE_WORDS 64

static uint32_t sec_boot2_copyout[BOOT2_SIZE_WORDS];
static bool sec_boot2_valid = false;

/* QMI CS1 window save state — flash_exit_xip() modifies these registers */
typedef struct {
    uint32_t timing;
    uint32_t rcmd;
    uint32_t rfmt;
} qmi_cs1_save_t;

static qmi_cs1_save_t sec_qmi_cs1_save;

static void __no_inline_not_in_flash_func(sec_flash_init_boot2_copyout)(void) {
    if (sec_boot2_valid)
        return;
    const volatile uint32_t *copy_from = (uint32_t *)BOOTRAM_BASE;
    for (int i = 0; i < BOOT2_SIZE_WORDS; ++i)
        sec_boot2_copyout[i] = copy_from[i];
    __compiler_memory_barrier();
    sec_boot2_valid = true;
}

static void __no_inline_not_in_flash_func(sec_flash_enable_xip_via_boot2)(void) {
    ((void (*)(void))((intptr_t)sec_boot2_copyout + 1))();
}

/* ====================================================================
 * Low-level SPI command via QMI direct mode — targets CS1.
 * ==================================================================== */

static void __no_inline_not_in_flash_func(sec_flash_do_cmd)(
        const uint8_t *txbuf, uint8_t *rxbuf, size_t count) {
    hw_set_bits(&qmi_hw->direct_csr, QMI_DIRECT_CSR_ASSERT_CS1N_BITS);
    hw_set_bits(&qmi_hw->direct_csr, QMI_DIRECT_CSR_EN_BITS);

    size_t tx_remaining = count;
    size_t rx_remaining = count;

    while (tx_remaining || rx_remaining) {
        uint32_t flags = qmi_hw->direct_csr;
        bool can_put = !(flags & QMI_DIRECT_CSR_TXFULL_BITS);
        bool can_get = !(flags & QMI_DIRECT_CSR_RXEMPTY_BITS);
        if (can_put && tx_remaining) {
            qmi_hw->direct_tx = *txbuf++;
            --tx_remaining;
        }
        if (can_get && rx_remaining) {
            *rxbuf++ = (uint8_t)qmi_hw->direct_rx;
            --rx_remaining;
        }
    }

    hw_clear_bits(&qmi_hw->direct_csr, QMI_DIRECT_CSR_EN_BITS);
    hw_clear_bits(&qmi_hw->direct_csr, QMI_DIRECT_CSR_ASSERT_CS1N_BITS);
}

/* ====================================================================
 * Initialization
 * ==================================================================== */

void sec_flash_init(void) {
    flash_devinfo_set_cs_gpio(1, FLASH_CS1_GPIO);
    flash_devinfo_set_cs_size(1, FLASH_DEVINFO_SIZE_16M);
    gpio_set_function(FLASH_CS1_GPIO, GPIO_FUNC_XIP_CS1);
}

/* ====================================================================
 * Bus acquire / release
 * ==================================================================== */

uint32_t __no_inline_not_in_flash_func(sec_flash_acquire_bus)(void) {
    sec_flash_init_boot2_copyout();

    uint32_t irq_state = save_and_disable_interrupts();

    xip_cache_clean_all();

    sec_qmi_cs1_save.timing = qmi_hw->m[1].timing;
    sec_qmi_cs1_save.rcmd   = qmi_hw->m[1].rcmd;
    sec_qmi_cs1_save.rfmt   = qmi_hw->m[1].rfmt;

    __compiler_memory_barrier();

    rom_connect_internal_flash_fn connect_internal_flash_func =
        (rom_connect_internal_flash_fn)rom_func_lookup_inline(ROM_FUNC_CONNECT_INTERNAL_FLASH);
    rom_flash_exit_xip_fn flash_exit_xip_func =
        (rom_flash_exit_xip_fn)rom_func_lookup_inline(ROM_FUNC_FLASH_EXIT_XIP);

    connect_internal_flash_func();
    flash_exit_xip_func();

    io_bank0_hw->io[FLASH_CS1_GPIO].ctrl = GPIO_FUNC_XIP_CS1;

    return irq_state;
}

void __no_inline_not_in_flash_func(sec_flash_release_bus)(uint32_t irq_state) {
    rom_flash_flush_cache_fn flash_flush_cache_func =
        (rom_flash_flush_cache_fn)rom_func_lookup_inline(ROM_FUNC_FLASH_FLUSH_CACHE);
    flash_flush_cache_func();

    sec_flash_enable_xip_via_boot2();

    qmi_hw->m[1].timing = sec_qmi_cs1_save.timing;
    qmi_hw->m[1].rcmd   = sec_qmi_cs1_save.rcmd;
    qmi_hw->m[1].rfmt   = sec_qmi_cs1_save.rfmt;
    qmi_hw->m[1].wfmt   = QMI_M1_WFMT_RESET;
    qmi_hw->m[1].wcmd   = QMI_M1_WCMD_RESET;

    restore_interrupts(irq_state);
}

/* ====================================================================
 * Flash command layer — all must be called with bus acquired
 * ==================================================================== */

static void __no_inline_not_in_flash_func(sec_flash_write_enable)(void) {
    uint8_t cmd = 0x06;
    uint8_t rx;
    sec_flash_do_cmd(&cmd, &rx, 1);
}

uint8_t __no_inline_not_in_flash_func(sec_flash_read_status)(void) {
    uint8_t tx[2] = {0x05, 0x00};
    uint8_t rx[2];
    sec_flash_do_cmd(tx, rx, 2);
    return rx[1];
}

static bool __no_inline_not_in_flash_func(sec_flash_wait_busy)(uint32_t max_polls) {
    for (uint32_t i = 0; i < max_polls; i++) {
        if (!(sec_flash_read_status() & 0x01))
            return true;
        tight_loop_contents();
    }
    return false;  /* timed out */
}

/* Poll limits (~1-2us per SPI read_status transaction):
 * Page program: 10ms max → 100,000 polls ≈ 100-200ms headroom
 * Sector erase: 400ms max → 500,000 polls ≈ 0.5-1s headroom */
#define WAIT_PAGE_PROGRAM   100000U
#define WAIT_SECTOR_ERASE   500000U

void __no_inline_not_in_flash_func(sec_flash_read_jedec_id)(uint8_t *mfr, uint8_t *type, uint8_t *cap) {
    uint8_t tx[4] = {0x9F, 0x00, 0x00, 0x00};
    uint8_t rx[4];
    sec_flash_do_cmd(tx, rx, 4);
    *mfr  = rx[1];
    *type = rx[2];
    *cap  = rx[3];
}

void __no_inline_not_in_flash_func(sec_flash_read)(uint32_t addr, uint8_t *buf, size_t len) {
    hw_set_bits(&qmi_hw->direct_csr, QMI_DIRECT_CSR_ASSERT_CS1N_BITS);
    hw_set_bits(&qmi_hw->direct_csr, QMI_DIRECT_CSR_EN_BITS);

    uint8_t hdr[4] = {
        0x03,
        (addr >> 16) & 0xFF,
        (addr >> 8) & 0xFF,
        addr & 0xFF
    };
    size_t tx_remaining = 4;
    size_t rx_remaining = 4;
    while (tx_remaining || rx_remaining) {
        uint32_t flags = qmi_hw->direct_csr;
        if (!(flags & QMI_DIRECT_CSR_TXFULL_BITS) && tx_remaining) {
            qmi_hw->direct_tx = hdr[4 - tx_remaining];
            --tx_remaining;
        }
        if (!(flags & QMI_DIRECT_CSR_RXEMPTY_BITS) && rx_remaining) {
            (void)qmi_hw->direct_rx;
            --rx_remaining;
        }
    }

    tx_remaining = len;
    rx_remaining = len;
    size_t rx_idx = 0;
    while (tx_remaining || rx_remaining) {
        uint32_t flags = qmi_hw->direct_csr;
        if (!(flags & QMI_DIRECT_CSR_TXFULL_BITS) && tx_remaining) {
            qmi_hw->direct_tx = 0x00;
            --tx_remaining;
        }
        if (!(flags & QMI_DIRECT_CSR_RXEMPTY_BITS) && rx_remaining) {
            buf[rx_idx++] = (uint8_t)qmi_hw->direct_rx;
            --rx_remaining;
        }
    }

    hw_clear_bits(&qmi_hw->direct_csr, QMI_DIRECT_CSR_EN_BITS);
    hw_clear_bits(&qmi_hw->direct_csr, QMI_DIRECT_CSR_ASSERT_CS1N_BITS);
}

bool __no_inline_not_in_flash_func(sec_flash_page_program)(uint32_t addr, const uint8_t *data, size_t len) {
    sec_flash_write_enable();

    hw_set_bits(&qmi_hw->direct_csr, QMI_DIRECT_CSR_ASSERT_CS1N_BITS);
    hw_set_bits(&qmi_hw->direct_csr, QMI_DIRECT_CSR_EN_BITS);

    uint8_t hdr[4] = {
        0x02,
        (addr >> 16) & 0xFF,
        (addr >> 8) & 0xFF,
        addr & 0xFF
    };

    size_t tx_remaining = 4;
    size_t rx_remaining = 4;
    while (tx_remaining || rx_remaining) {
        uint32_t flags = qmi_hw->direct_csr;
        if (!(flags & QMI_DIRECT_CSR_TXFULL_BITS) && tx_remaining) {
            qmi_hw->direct_tx = hdr[4 - tx_remaining];
            --tx_remaining;
        }
        if (!(flags & QMI_DIRECT_CSR_RXEMPTY_BITS) && rx_remaining) {
            (void)qmi_hw->direct_rx;
            --rx_remaining;
        }
    }

    tx_remaining = len;
    rx_remaining = len;
    size_t tx_idx = 0;
    while (tx_remaining || rx_remaining) {
        uint32_t flags = qmi_hw->direct_csr;
        if (!(flags & QMI_DIRECT_CSR_TXFULL_BITS) && tx_remaining) {
            qmi_hw->direct_tx = data[tx_idx++];
            --tx_remaining;
        }
        if (!(flags & QMI_DIRECT_CSR_RXEMPTY_BITS) && rx_remaining) {
            (void)qmi_hw->direct_rx;
            --rx_remaining;
        }
    }

    hw_clear_bits(&qmi_hw->direct_csr, QMI_DIRECT_CSR_EN_BITS);
    hw_clear_bits(&qmi_hw->direct_csr, QMI_DIRECT_CSR_ASSERT_CS1N_BITS);

    return sec_flash_wait_busy(WAIT_PAGE_PROGRAM);
}

bool __no_inline_not_in_flash_func(sec_flash_sector_erase)(uint32_t addr) {
    sec_flash_write_enable();

    uint8_t tx[4] = {
        0x20,
        (addr >> 16) & 0xFF,
        (addr >> 8) & 0xFF,
        addr & 0xFF
    };
    uint8_t rx[4];
    sec_flash_do_cmd(tx, rx, 4);

    return sec_flash_wait_busy(WAIT_SECTOR_ERASE);
}
