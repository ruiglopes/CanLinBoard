#ifndef CONFIG_PROTOCOL_H
#define CONFIG_PROTOCOL_H

/* ---- Command Codes (byte[0] of 0x600 frame) ---- */
#define CFG_CMD_CONNECT             0x01
#define CFG_CMD_SAVE                0x02
#define CFG_CMD_DEFAULTS            0x03
#define CFG_CMD_REBOOT              0x04
#define CFG_CMD_ENTER_BOOTLOADER    0x05
#define CFG_CMD_GET_STATUS          0x06
#define CFG_CMD_READ_PARAM          0x10
#define CFG_CMD_WRITE_PARAM         0x11
#define CFG_CMD_BULK_START          0x20
#define CFG_CMD_BULK_END            0x21
#define CFG_CMD_BULK_READ           0x22

/* ---- Section IDs (byte[1] for READ/WRITE) ---- */
#define CFG_SECTION_CAN             0x00
#define CFG_SECTION_LIN             0x01
#define CFG_SECTION_ROUTING         0x02  /* Bulk only */
#define CFG_SECTION_DIAG            0x03
#define CFG_SECTION_PROFILES        0x04
#define CFG_SECTION_DEVICE          0x05

/* ---- Response Status Codes ---- */
#define CFG_STATUS_OK               0x00
#define CFG_STATUS_UNKNOWN_CMD      0x01
#define CFG_STATUS_INVALID_PARAM    0x02
#define CFG_STATUS_CRC_MISMATCH     0x03
#define CFG_STATUS_NVM_ERROR        0x04
#define CFG_STATUS_BUSY             0x05

#endif /* CONFIG_PROTOCOL_H */
