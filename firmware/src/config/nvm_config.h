#ifndef NVM_CONFIG_H
#define NVM_CONFIG_H

#include <stdint.h>
#include <stdbool.h>
#include "board_config.h"
#include "lin/lin_bus.h"
#include "gateway/gateway_engine.h"

/* ---- NVM Configuration Struct ---- */

typedef struct __attribute__((packed)) {
    /* Header (12 bytes) */
    uint32_t magic;          /* NVM_CONFIG_MAGIC */
    uint16_t version;        /* NVM_CONFIG_VERSION */
    uint16_t size;           /* sizeof(nvm_config_t) */
    uint32_t write_count;    /* Incremented on each save */

    /* CAN config (2 buses) */
    struct __attribute__((packed)) {
        uint32_t bitrate;
        uint8_t  termination;   /* bool */
        uint8_t  enabled;       /* bool */
        uint8_t  _pad[2];
    } can[2];

    /* LIN config (4 channels) */
    struct __attribute__((packed)) {
        uint8_t  enabled;       /* bool */
        uint8_t  mode;          /* lin_mode_t */
        uint8_t  _pad[2];
        uint32_t baudrate;
        lin_schedule_table_t schedule;
    } lin[LIN_CHANNEL_COUNT];

    /* Routing rules */
    uint8_t        routing_rule_count;
    routing_rule_t routing_rules[MAX_ROUTING_RULES];

    /* Diagnostics config */
    struct __attribute__((packed)) {
        uint32_t can_id;
        uint16_t interval_ms;
        uint8_t  bus;           /* 0=CAN1, 1=CAN2 */
        uint8_t  enabled;       /* bool */
        uint16_t can_watchdog_ms;  /* 0=disabled */
        uint16_t lin_watchdog_ms;  /* 0=disabled */
    } diag;

    /* Device profiles */
    struct __attribute__((packed)) {
        uint8_t wda_enabled;
        uint8_t wda_channel;
        uint8_t cwa400_enabled;
        uint8_t cwa400_channel;
        uint8_t _pad[4];
    } profiles;

    /* CRC32 — last field, computed over all preceding bytes */
    uint32_t crc32;
} nvm_config_t;

/* ---- NVM Metadata Sector ---- */

typedef struct __attribute__((packed)) {
    uint32_t magic;          /* NVM_META_MAGIC */
    uint8_t  active_slot;    /* 0 = Slot A, 1 = Slot B */
    uint8_t  _pad[3];
    uint32_t write_count;
    uint32_t crc32;
} nvm_meta_t;

/* ---- Public API ---- */

/**
 * Load config from NVM.
 * @return true if valid config found, false if defaults used.
 */
bool nvm_config_load(nvm_config_t *cfg);

/**
 * Save config to NVM (ping-pong: writes to inactive slot, updates meta).
 * @return true on success.
 */
bool nvm_config_save(const nvm_config_t *cfg);

/**
 * Fill config with factory defaults.
 */
void nvm_config_defaults(nvm_config_t *cfg);

/**
 * Validate CRC32 of a config struct.
 */
bool nvm_config_validate(const nvm_config_t *cfg);

/**
 * Get the current write count from meta sector.
 */
uint32_t nvm_config_get_write_count(void);

#endif /* NVM_CONFIG_H */
