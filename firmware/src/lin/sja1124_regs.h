#ifndef SJA1124_REGS_H
#define SJA1124_REGS_H

/*
 * SJA1124 LIN Transceiver — Complete Register Map
 * Based on NXP SJA1124 datasheet Rev. 2, 26 August 2022.
 */

/* ==== System Control Registers ==== */
#define SJA_REG_MODE            0x00
#define SJA_REG_PLLCFG          0x01
#define SJA_REG_INT1EN          0x02
#define SJA_REG_INT2EN          0x03
#define SJA_REG_INT3EN          0x04

/* ==== System Status Registers ==== */
#define SJA_REG_INT1            0x10
#define SJA_REG_INT2            0x11
#define SJA_REG_INT3            0x12
#define SJA_REG_STATUS          0x13

/* ==== Commander Global Registers ==== */
#define SJA_REG_LCOM1           0x20
#define SJA_REG_LCOM2           0x21

/* ==== Commander Termination ==== */
#define SJA_REG_MCFG            0xF0
#define SJA_REG_MMTPS           0xF1
#define SJA_REG_MCFGCRC         0xF2

/* ==== Other ==== */
#define SJA_REG_MTPCS           0xFE
#define SJA_REG_ID              0xFF

/* ==== Per-Channel Register Base Addresses ==== */
#define SJA_CH1_BASE            0x30
#define SJA_CH2_BASE            0x60
#define SJA_CH3_BASE            0x90
#define SJA_CH4_BASE            0xC0

/* Channel base from index (0-3) */
#define SJA_CH_BASE(ch)         (0x30 * ((ch) + 1))

/* ==== Per-Channel Register Offsets (from channel base) ==== */
/* Initialization registers */
#define SJA_OFF_LCFG1           0x00    /* Configuration 1 (SLEEP, INIT, MBL, CCD) */
#define SJA_OFF_LCFG2           0x01    /* Configuration 2 (TBDE, IOBE) */
#define SJA_OFF_LITC            0x02    /* Idle timeout control (IOT) */
#define SJA_OFF_LGC             0x03    /* Global control (STOP, SR) */
#define SJA_OFF_LRTC            0x04    /* Response timeout control (RTO) */
#define SJA_OFF_LFR             0x05    /* Fractional baud rate (FBR) */
#define SJA_OFF_LBRM            0x06    /* Baud rate MSB (IBR[15:8]) */
#define SJA_OFF_LBRL            0x07    /* Baud rate LSB (IBR[7:0]) */
#define SJA_OFF_LIE             0x08    /* Interrupt enable */

/* Send frame registers */
#define SJA_OFF_LC              0x09    /* Control (HTRQ, ABRQ, WURQ) */
#define SJA_OFF_LBI             0x0A    /* Buffer identifier (6-bit ID) */
#define SJA_OFF_LBC             0x0B    /* Buffer control (DFL, DIR, CCS) */
#define SJA_OFF_LCF             0x0C    /* Checksum field */
#define SJA_OFF_LBD1            0x0D    /* Buffer data byte 1 */
#define SJA_OFF_LBD2            0x0E    /* Buffer data byte 2 */
#define SJA_OFF_LBD3            0x0F    /* Buffer data byte 3 */
#define SJA_OFF_LBD4            0x10    /* Buffer data byte 4 */
#define SJA_OFF_LBD5            0x11    /* Buffer data byte 5 */
#define SJA_OFF_LBD6            0x12    /* Buffer data byte 6 */
#define SJA_OFF_LBD7            0x13    /* Buffer data byte 7 */
#define SJA_OFF_LBD8            0x14    /* Buffer data byte 8 */

/* Get status registers */
#define SJA_OFF_LSTATE          0x1F    /* LIN state machine state */
#define SJA_OFF_LES             0x20    /* Error status */
#define SJA_OFF_LS              0x21    /* Status (DRF, DTF, DRBNE) */
#define SJA_OFF_LCF_RX          0x22    /* Received checksum */
#define SJA_OFF_LBD1_RX         0x23    /* Received data byte 1 */
#define SJA_OFF_LBD2_RX         0x24
#define SJA_OFF_LBD3_RX         0x25
#define SJA_OFF_LBD4_RX         0x26
#define SJA_OFF_LBD5_RX         0x27
#define SJA_OFF_LBD6_RX         0x28
#define SJA_OFF_LBD7_RX         0x29
#define SJA_OFF_LBD8_RX         0x2A

/* ==== MODE Register (0x00) ==== */
#define SJA_MODE_RST            (1 << 7)
#define SJA_MODE_LPMODE         (1 << 0)

/* ==== PLLCFG Register (0x01) ==== */
#define SJA_PLLCFG_MASK         0x0F

/* PLLMULT values for various input clock ranges */
#define SJA_PLLMULT_0_4_0_5MHZ 0x0     /* M=78 */
#define SJA_PLLMULT_0_5_0_7MHZ 0x1     /* M=65 */
#define SJA_PLLMULT_0_7_1_0MHZ 0x2     /* M=39 */
#define SJA_PLLMULT_1_0_1_4MHZ 0x3     /* M=28 */
#define SJA_PLLMULT_1_4_1_9MHZ 0x4     /* M=20 */
#define SJA_PLLMULT_1_9_2_6MHZ 0x5     /* M=15 */
#define SJA_PLLMULT_2_6_3_5MHZ 0x6     /* M=11 */
#define SJA_PLLMULT_3_5_4_5MHZ 0x7     /* M=8.5 */
#define SJA_PLLMULT_4_5_6_0MHZ 0x8     /* M=6.4 */
#define SJA_PLLMULT_6_0_8_0MHZ 0x9     /* M=4.8 */
#define SJA_PLLMULT_8_0_10MHZ  0xA     /* M=3.9 (default, for 8 MHz input) */

/* PLL output frequencies (approximate, for 8 MHz input with M=3.9) */
#define SJA_PLL_OUTPUT_FREQ_8MHZ    31200000U   /* 3.9 * 8 MHz */

/* ==== INT1EN Register (0x02) ==== */
#define SJA_INT1EN_L4WUIE      (1 << 3)
#define SJA_INT1EN_L3WUIE      (1 << 2)
#define SJA_INT1EN_L2WUIE      (1 << 1)
#define SJA_INT1EN_L1WUIE      (1 << 0)

/* ==== INT2EN Register (0x03) ==== */
#define SJA_INT2EN_OTWIE       (1 << 5)
#define SJA_INT2EN_PLLOLIE     (1 << 4)
#define SJA_INT2EN_PLLILIE     (1 << 3)
#define SJA_INT2EN_PLLIFFIE    (1 << 2)
#define SJA_INT2EN_SPIEIE      (1 << 1)

/* ==== INT3EN Register (0x04) ==== */
#define SJA_INT3EN_L4EIE       (1 << 7)
#define SJA_INT3EN_L3EIE       (1 << 6)
#define SJA_INT3EN_L2EIE       (1 << 5)
#define SJA_INT3EN_L1EIE       (1 << 4)
#define SJA_INT3EN_L4SIE       (1 << 3)
#define SJA_INT3EN_L3SIE       (1 << 2)
#define SJA_INT3EN_L2SIE       (1 << 1)
#define SJA_INT3EN_L1SIE       (1 << 0)

/* ==== INT1 Register (0x10) ==== */
#define SJA_INT1_INITI         (1 << 7)
#define SJA_INT1_L4WUI         (1 << 3)
#define SJA_INT1_L3WUI         (1 << 2)
#define SJA_INT1_L2WUI         (1 << 1)
#define SJA_INT1_L1WUI         (1 << 0)

/* ==== INT2 Register (0x11) ==== */
#define SJA_INT2_OTWI          (1 << 5)
#define SJA_INT2_PLLOLI        (1 << 4)
#define SJA_INT2_PLLILI        (1 << 3)
#define SJA_INT2_PLLIFFI       (1 << 2)
#define SJA_INT2_SPIEI         (1 << 1)
#define SJA_INT2_LPRFI         (1 << 0)

/* ==== INT3 Register (0x12) ==== */
#define SJA_INT3_L4EI          (1 << 7)
#define SJA_INT3_L3EI          (1 << 6)
#define SJA_INT3_L2EI          (1 << 5)
#define SJA_INT3_L1EI          (1 << 4)
#define SJA_INT3_L4SI          (1 << 3)
#define SJA_INT3_L3SI          (1 << 2)
#define SJA_INT3_L2SI          (1 << 1)
#define SJA_INT3_L1SI          (1 << 0)

/* Per-channel INT3 masks */
#define SJA_INT3_ERR_MASK(ch)  (1 << (4 + (ch)))   /* ch 0-3 → bits 4-7 */
#define SJA_INT3_STS_MASK(ch)  (1 << (ch))          /* ch 0-3 → bits 0-3 */

/* ==== STATUS Register (0x13) ==== */
#define SJA_STATUS_OTW         (1 << 5)
#define SJA_STATUS_PLLIL       (1 << 3)    /* PLL in lock */
#define SJA_STATUS_PLLIFF      (1 << 2)    /* PLL input freq fail */

/* ==== LCOM1 Register (0x20) ==== */
#define SJA_LCOM1_HS(ch)       (1 << (4 + (ch)))   /* High-speed enable ch 0-3 */
#define SJA_LCOM1_TMFE(ch)     (1 << (ch))          /* TMF sync TX enable ch 0-3 */

/* ==== LCOM2 Register (0x21) ==== */
#define SJA_LCOM2_HTRQ(ch)    (1 << (ch))  /* Header TX request ch 0-3 */

/* ==== LCFG1 (per-channel) ==== */
#define SJA_LCFG1_CCD          (1 << 7)    /* Checksum calc disable */
#define SJA_LCFG1_MBL_MASK     (0x0F << 3) /* Commander break length [6:3] */
#define SJA_LCFG1_MBL_SHIFT    3
#define SJA_LCFG1_SLEEP        (1 << 1)
#define SJA_LCFG1_INIT         (1 << 0)

/* MBL values (break length in bits) */
#define SJA_MBL_10BIT          0x0
#define SJA_MBL_11BIT          0x1
#define SJA_MBL_12BIT          0x2
#define SJA_MBL_13BIT          0x3  /* LIN 2.x standard */
#define SJA_MBL_14BIT          0x4
#define SJA_MBL_15BIT          0x5
#define SJA_MBL_16BIT          0x6
#define SJA_MBL_17BIT          0x7
#define SJA_MBL_18BIT          0x8
#define SJA_MBL_19BIT          0x9
#define SJA_MBL_20BIT          0xA
#define SJA_MBL_21BIT          0xB
#define SJA_MBL_22BIT          0xC
#define SJA_MBL_23BIT          0xD
#define SJA_MBL_36BIT          0xE
#define SJA_MBL_50BIT          0xF

/* ==== LCFG2 (per-channel) ==== */
#define SJA_LCFG2_TBDE         (1 << 7)    /* 2-bit delimiter */
#define SJA_LCFG2_IOBE         (1 << 6)    /* Idle on bit error */

/* ==== LITC (per-channel) ==== */
#define SJA_LITC_IOT           (1 << 1)    /* Idle on timeout */

/* ==== LGC (per-channel) ==== */
#define SJA_LGC_STOP           (1 << 1)    /* Two stop bits */
#define SJA_LGC_SR             (1 << 0)    /* Soft reset */

/* ==== LRTC (per-channel) ==== */
#define SJA_LRTC_RTO_MASK      0x0F

/* ==== LFR (per-channel) ==== */
#define SJA_LFR_FBR_MASK       0x0F

/* ==== LIE (per-channel) ==== */
#define SJA_LIE_SZIE           (1 << 7)    /* Stuck-at-zero IE */
#define SJA_LIE_TOIE           (1 << 6)    /* Timeout IE */
#define SJA_LIE_BEIE           (1 << 5)    /* Bit error IE */
#define SJA_LIE_CEIE           (1 << 4)    /* Checksum error IE */
#define SJA_LIE_DRIE           (1 << 2)    /* Data reception IE */
#define SJA_LIE_DTIE           (1 << 1)    /* Data transmission IE */
#define SJA_LIE_FEIE           (1 << 0)    /* Frame error IE */
#define SJA_LIE_ALL_ERR        (SJA_LIE_SZIE | SJA_LIE_TOIE | SJA_LIE_BEIE | \
                                SJA_LIE_CEIE | SJA_LIE_FEIE)
#define SJA_LIE_ALL            (SJA_LIE_ALL_ERR | SJA_LIE_DRIE | SJA_LIE_DTIE)

/* ==== LC (per-channel) ==== */
#define SJA_LC_WURQ             (1 << 4)    /* Wake-up request */
#define SJA_LC_ABRQ             (1 << 1)    /* Abort request */
#define SJA_LC_HTRQ             (1 << 0)    /* Header TX request */

/* ==== LBI (per-channel) ==== */
#define SJA_LBI_ID_MASK         0x3F

/* ==== LBC (per-channel) ==== */
#define SJA_LBC_DFL_MASK        (0x07 << 2) /* Data field length [4:2] */
#define SJA_LBC_DFL_SHIFT       2
#define SJA_LBC_DIR             (1 << 1)    /* 1=TX (commander sends response) */
#define SJA_LBC_CCS             (1 << 0)    /* 1=Classic checksum, 0=Enhanced */

/* ==== LSTATE (per-channel) ==== */
#define SJA_LSTATE_RXBSY        (1 << 7)
#define SJA_LSTATE_LINS_MASK    0x0F

/* LINS state machine values */
#define SJA_LINS_SLEEP          0x0
#define SJA_LINS_INIT           0x1
#define SJA_LINS_IDLE           0x2
#define SJA_LINS_BREAK_TX       0x3
#define SJA_LINS_BREAK_DELIM    0x4
#define SJA_LINS_SYNC_TX        0x5
#define SJA_LINS_ID_TX          0x6
#define SJA_LINS_HEADER_DONE    0x7
#define SJA_LINS_RESPONSE       0x8
#define SJA_LINS_CHECKSUM       0x9

/* ==== LES (per-channel) ==== */
#define SJA_LES_SZF             (1 << 7)    /* Stuck-at-zero */
#define SJA_LES_TOF             (1 << 6)    /* Timeout */
#define SJA_LES_BEF             (1 << 5)    /* Bit error */
#define SJA_LES_CEF             (1 << 4)    /* Checksum error */
#define SJA_LES_FEF             (1 << 0)    /* Frame error */
#define SJA_LES_ALL             (SJA_LES_SZF | SJA_LES_TOF | SJA_LES_BEF | \
                                 SJA_LES_CEF | SJA_LES_FEF)

/* ==== LS (per-channel) ==== */
#define SJA_LS_DRBNE            (1 << 6)    /* Data RX buffer not empty */
#define SJA_LS_DRF              (1 << 2)    /* Data reception complete */
#define SJA_LS_DTF              (1 << 1)    /* Data transmission complete */

/* ==== SPI Control Byte ==== */
#define SJA_SPI_RO              (1 << 7)    /* Read-only flag */
#define SJA_SPI_DLC(n)          ((n) - 1)   /* DLC = num_bytes - 1 */

#endif /* SJA1124_REGS_H */
