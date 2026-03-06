#ifndef GATEWAY_ENGINE_H
#define GATEWAY_ENGINE_H

#include <stdint.h>
#include <stdbool.h>
#include "can/can_bus.h"
#include "board_config.h"
#include "FreeRTOS.h"
#include "queue.h"

/* ---- Sentinel Values ---- */
#define GW_DST_ID_PASSTHROUGH   0xFFFFFFFFU  /* Keep source frame ID */

/* ---- Byte Mapping (per-byte transform) ---- */
typedef struct {
    uint8_t src_byte;   /* Source data index (0-7) */
    uint8_t dst_byte;   /* Dest data index (0-7) */
    uint8_t mask;       /* Applied to source BEFORE shift (0xFF = whole byte) */
    int8_t  shift;      /* Positive = left shift, negative = right shift */
    int8_t  offset;     /* Added after mask+shift (signed) */
} byte_mapping_t;

/* ---- Routing Rule (unified for all bus combinations) ---- */
typedef struct {
    bus_id_t    src_bus;                        /* BUS_CAN1..BUS_LIN4 */
    uint32_t    src_id;                         /* Frame ID to match */
    uint32_t    src_mask;                       /* ID mask (0xFFFFFFFF = exact) */
    bus_id_t    dst_bus;                        /* Destination bus */
    uint32_t    dst_id;                         /* Dest ID (GW_DST_ID_PASSTHROUGH = keep) */
    uint8_t     dst_dlc;                        /* DLC override (0 = use source DLC) */
    uint8_t     mapping_count;                  /* 0 = full passthrough */
    byte_mapping_t mappings[MAX_BYTE_MAPPINGS]; /* Up to 8 byte transforms */
    bool        enabled;
} routing_rule_t;

_Static_assert(sizeof(byte_mapping_t) == 5, "byte_mapping_t size changed");
_Static_assert(sizeof(routing_rule_t) == 64, "routing_rule_t size changed");

/* ---- Gateway Statistics ---- */
typedef struct {
    uint32_t frames_routed;     /* Matched >= 1 rule */
    uint32_t frames_dropped;    /* Matched 0 rules */
    uint32_t can_tx_overflow;   /* CAN TX queue full */
    uint32_t lin_tx_overflow;   /* LIN TX queue full */
} gateway_stats_t;

/* ---- Public API ---- */

/**
 * Initialize the gateway engine with TX queue handles.
 * Must be called before gateway_engine_process().
 */
void gateway_engine_init(QueueHandle_t can_tx_q, QueueHandle_t lin_tx_q);

/**
 * Add a routing rule. Returns rule index (0..MAX_ROUTING_RULES-1) or -1 if full.
 */
int gateway_engine_add_rule(const routing_rule_t *rule);

/**
 * Remove a routing rule by index.
 */
bool gateway_engine_remove_rule(uint8_t index);

/**
 * Enable or disable a routing rule by index.
 */
bool gateway_engine_enable_rule(uint8_t index, bool enable);

/**
 * Read back a routing rule by index.
 */
bool gateway_engine_get_rule(uint8_t index, routing_rule_t *out);

/**
 * Get the number of active (occupied) rule slots.
 */
uint8_t gateway_engine_get_rule_count(void);

/**
 * Remove all routing rules.
 */
void gateway_engine_clear_rules(void);

/**
 * Atomically replace all routing rules (critical section protected).
 * Safe to call from a different task than gateway_engine_process().
 * Accepts void* to avoid packed-member alignment warnings from NVM config.
 */
void gateway_engine_replace_rules(const void *rules, uint8_t count);

/**
 * Process a single inbound frame through all routing rules.
 * Matched frames are dispatched to the appropriate TX queue.
 * Multiple rules may match (fan-out).
 */
void gateway_engine_process(const gateway_frame_t *frame);

/**
 * Read current gateway statistics.
 */
void gateway_engine_get_stats(gateway_stats_t *stats);

/**
 * Reset all gateway statistics to zero.
 */
void gateway_engine_reset_stats(void);

#endif /* GATEWAY_ENGINE_H */
