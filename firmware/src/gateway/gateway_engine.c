#include "gateway/gateway_engine.h"
#include "FreeRTOS.h"
#include "task.h"
#include <string.h>

/* ---- Module State ---- */
static routing_rule_t s_rules[MAX_ROUTING_RULES];
static uint8_t        s_rule_count;
static gateway_stats_t s_stats;
static QueueHandle_t  s_can_tx_queue;
static QueueHandle_t  s_lin_tx_queue;

/* ---- Rule Matching ---- */

static bool rule_matches(const routing_rule_t *rule, const gateway_frame_t *frame)
{
    if (!rule->enabled) return false;
    if (rule->src_bus != frame->source_bus) return false;
    return (frame->frame.id & rule->src_mask) == (rule->src_id & rule->src_mask);
}

/* ---- Frame Transformation ---- */

static void apply_rule(const routing_rule_t *rule, const gateway_frame_t *in,
                        gateway_frame_t *out)
{
    /* Set destination bus (downstream TX paths use source_bus as selector) */
    out->source_bus = rule->dst_bus;
    out->timestamp = in->timestamp;

    /* ID: passthrough or override */
    if (rule->dst_id == GW_DST_ID_PASSTHROUGH) {
        out->frame.id = in->frame.id;
    } else {
        out->frame.id = rule->dst_id;
    }

    /* DLC: override if non-zero, else use source */
    out->frame.dlc = (rule->dst_dlc != 0) ? rule->dst_dlc : in->frame.dlc;

    /* Flags: preserve for CAN destinations, clear for LIN */
    if (rule->dst_bus <= BUS_CAN2) {
        out->frame.flags = in->frame.flags;
    } else {
        out->frame.flags = 0;
    }

    /* Data transformation */
    if (rule->mapping_count == 0) {
        /* Full passthrough — copy all 8 bytes */
        memcpy(out->frame.data, in->frame.data, 8);
    } else {
        /* Zero destination, then apply each mapping */
        memset(out->frame.data, 0, 8);
        for (uint8_t i = 0; i < rule->mapping_count; i++) {
            const byte_mapping_t *m = &rule->mappings[i];
            if (m->src_byte > 7 || m->dst_byte > 7) continue;

            uint8_t val = in->frame.data[m->src_byte] & m->mask;
            if (m->shift > 0) {
                val = val << m->shift;
            } else if (m->shift < 0) {
                val = val >> (-m->shift);
            }
            val = (uint8_t)((int16_t)val + m->offset);

            /* OR allows multiple mappings to compose into same byte */
            out->frame.data[m->dst_byte] |= val;
        }
    }
}

/* ---- Dispatch ---- */

static void dispatch_frame(const gateway_frame_t *frame)
{
    if (frame->source_bus <= BUS_CAN2) {
        if (xQueueSend(s_can_tx_queue, frame, 0) != pdTRUE) {
            s_stats.can_tx_overflow++;
        }
    } else {
        if (xQueueSend(s_lin_tx_queue, frame, 0) != pdTRUE) {
            s_stats.lin_tx_overflow++;
        }
    }
}

/* ---- Public API ---- */

void gateway_engine_init(QueueHandle_t can_tx_q, QueueHandle_t lin_tx_q)
{
    s_can_tx_queue = can_tx_q;
    s_lin_tx_queue = lin_tx_q;
    s_rule_count = 0;
    memset(s_rules, 0, sizeof(s_rules));
    memset(&s_stats, 0, sizeof(s_stats));
}

int gateway_engine_add_rule(const routing_rule_t *rule)
{
    if (s_rule_count >= MAX_ROUTING_RULES) return -1;

    /* Find first empty slot */
    for (uint8_t i = 0; i < MAX_ROUTING_RULES; i++) {
        if (!s_rules[i].enabled && s_rules[i].src_mask == 0) {
            s_rules[i] = *rule;
            s_rule_count++;
            return (int)i;
        }
    }
    return -1;
}

bool gateway_engine_remove_rule(uint8_t index)
{
    if (index >= MAX_ROUTING_RULES) return false;
    if (!s_rules[index].enabled && s_rules[index].src_mask == 0) return false;

    memset(&s_rules[index], 0, sizeof(routing_rule_t));
    if (s_rule_count > 0) s_rule_count--;
    return true;
}

bool gateway_engine_enable_rule(uint8_t index, bool enable)
{
    if (index >= MAX_ROUTING_RULES) return false;
    /* Only toggle if slot is occupied (src_mask != 0 or was added) */
    s_rules[index].enabled = enable;
    return true;
}

bool gateway_engine_get_rule(uint8_t index, routing_rule_t *out)
{
    if (index >= MAX_ROUTING_RULES) return false;
    *out = s_rules[index];
    return true;
}

uint8_t gateway_engine_get_rule_count(void)
{
    return s_rule_count;
}

void gateway_engine_clear_rules(void)
{
    memset(s_rules, 0, sizeof(s_rules));
    s_rule_count = 0;
}

void gateway_engine_replace_rules(const void *rules, uint8_t count)
{
    if (count > MAX_ROUTING_RULES) count = MAX_ROUTING_RULES;

    /* Atomic swap: critical section prevents gateway_engine_process() from
     * seeing a half-cleared/half-loaded rule table. */
    taskENTER_CRITICAL();
    memset(s_rules, 0, sizeof(s_rules));
    if (count > 0) {
        memcpy(s_rules, rules, count * sizeof(routing_rule_t));
    }
    s_rule_count = count;
    taskEXIT_CRITICAL();
}

void gateway_engine_process(const gateway_frame_t *frame)
{
    bool matched = false;

    for (uint8_t i = 0; i < MAX_ROUTING_RULES; i++) {
        if (rule_matches(&s_rules[i], frame)) {
            gateway_frame_t out;
            apply_rule(&s_rules[i], frame, &out);
            dispatch_frame(&out);
            matched = true;
        }
    }

    if (matched) {
        s_stats.frames_routed++;
    } else {
        s_stats.frames_dropped++;
    }
}

void gateway_engine_get_stats(gateway_stats_t *stats)
{
    *stats = s_stats;
}

void gateway_engine_reset_stats(void)
{
    memset(&s_stats, 0, sizeof(s_stats));
}
