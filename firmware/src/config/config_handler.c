#include "config/config_handler.h"
#include "config/config_protocol.h"
#include "config/nvm_config.h"
#include "can/can_bus.h"
#include "can/can_manager.h"
#include "lin/lin_manager.h"
#include "gateway/gateway_engine.h"
#include "diag/diagnostics.h"
#include "diag/bus_watchdog.h"
#include "monitor/bus_monitor.h"
#include "logger/flash_logger.h"
#include "hal/hal_gpio.h"
#include "util/crc32.h"
#include "board_config.h"
#include "app_header.h"

#include "hardware/watchdog.h"
#include "FreeRTOS.h"
#include "queue.h"
#include "semphr.h"

#include <string.h>

/* ---- State ---- */

static nvm_config_t s_working_config;
static QueueHandle_t s_config_rx_queue;
static QueueHandle_t s_can_tx_queue;
static SemaphoreHandle_t s_config_mutex;

/* Bulk transfer state */
static bool     s_bulk_active;
static uint8_t  s_bulk_section;
static uint8_t  s_bulk_sub;         /* Sub-index (LIN channel for section=0x01) */
static uint16_t s_bulk_expected_size;
static uint32_t s_bulk_expected_crc;
static uint16_t s_bulk_received;
static uint8_t  s_bulk_seq;
/* Staging buffer: MAX_ROUTING_RULES * sizeof(routing_rule_t) */
static uint8_t  s_bulk_buffer[MAX_ROUTING_RULES * sizeof(routing_rule_t)];

/* ---- Response Helper ---- */

static void send_response(uint8_t cmd, uint8_t status,
                           const uint8_t *payload, uint8_t payload_len)
{
    can_frame_t frame = {0};
    frame.id  = CONFIG_CAN_RESP_ID;
    frame.dlc = 2 + (payload_len > 6 ? 6 : payload_len);
    frame.data[0] = cmd;
    frame.data[1] = status;
    if (payload && payload_len > 0) {
        uint8_t n = payload_len > 6 ? 6 : payload_len;
        memcpy(&frame.data[2], payload, n);
    }
    /* Retry if TX buffer is busy (e.g., diagnostic heartbeat in flight) */
    for (int i = 0; i < 5; i++) {
        if (can_manager_transmit(CAN_BUS_1, &frame)) return;
        vTaskDelay(pdMS_TO_TICKS(1));
    }
}

/* ---- Runtime Apply ---- */

static void apply_config(const nvm_config_t *cfg)
{
    /* CAN termination */
    hal_can_set_termination(CAN_BUS_1, cfg->can[0].termination);
    hal_can_set_termination(CAN_BUS_2, cfg->can[1].termination);

    /* CAN2: stop first (safe if not running), then start/stop based on config */
    can_manager_stop_can2();
    if (cfg->can[1].enabled) {
        can_manager_start_can2(cfg->can[1].bitrate);
        bus_watchdog_set_enabled(BUS_CAN2, true);
    } else {
        can_manager_stop_can2();
        bus_watchdog_set_enabled(BUS_CAN2, false);
    }

    /* LIN channels — stop first, then restart enabled ones */
    for (int ch = 0; ch < LIN_CHANNEL_COUNT; ch++) {
        lin_manager_stop_channel(ch);
        bus_watchdog_set_enabled((bus_id_t)(BUS_LIN1 + ch), false);
    }
    for (int ch = 0; ch < LIN_CHANNEL_COUNT; ch++) {
        if (cfg->lin[ch].enabled) {
            lin_channel_config_t lc;
            lc.enabled  = true;
            lc.mode     = (lin_mode_t)cfg->lin[ch].mode;
            lc.baudrate = cfg->lin[ch].baudrate;
            memcpy(&lc.schedule, &cfg->lin[ch].schedule, sizeof(lin_schedule_table_t));
            lin_manager_start_channel(ch, &lc);
            bus_watchdog_set_enabled((bus_id_t)(BUS_LIN1 + ch), true);
        }
    }

    /* Gateway routing rules (atomic swap — safe across tasks) */
    gateway_engine_replace_rules(cfg->routing_rules, cfg->routing_rule_count);

    /* Diagnostics reconfigure (interval, bus, watchdog timeouts) */
    diagnostics_reconfigure();
}

/* ---- Command Handlers ---- */

static void handle_connect(void)
{
    uint8_t payload[6];
    payload[0] = FW_VERSION_MAJOR;
    payload[1] = FW_VERSION_MINOR;
    payload[2] = FW_VERSION_PATCH;
    /* Config size (LE) */
    uint16_t sz = sizeof(nvm_config_t);
    payload[3] = (uint8_t)(sz);
    payload[4] = (uint8_t)(sz >> 8);
    payload[5] = s_working_config.routing_rule_count;
    send_response(CFG_CMD_CONNECT, CFG_STATUS_OK, payload, 6);
}

static void handle_get_status(void)
{
    uint32_t wc = nvm_config_get_write_count();
    uint8_t payload[4];
    payload[0] = (uint8_t)(wc);
    payload[1] = (uint8_t)(wc >> 8);
    payload[2] = (uint8_t)(wc >> 16);
    payload[3] = (uint8_t)(wc >> 24);
    send_response(CFG_CMD_GET_STATUS, CFG_STATUS_OK, payload, 4);
}

static void handle_save(void)
{
    if (nvm_config_save(&s_working_config)) {
        apply_config(&s_working_config);
        send_response(CFG_CMD_SAVE, CFG_STATUS_OK, NULL, 0);
    } else {
        send_response(CFG_CMD_SAVE, CFG_STATUS_NVM_ERROR, NULL, 0);
    }
}

static void handle_defaults(void)
{
    config_handler_lock();
    nvm_config_defaults(&s_working_config);
    config_handler_unlock();
    send_response(CFG_CMD_DEFAULTS, CFG_STATUS_OK, NULL, 0);
}

static void handle_reboot(void)
{
    send_response(CFG_CMD_REBOOT, CFG_STATUS_OK, NULL, 0);
    vTaskDelay(pdMS_TO_TICKS(50));  /* Let CAN TX complete */
    watchdog_reboot(0, 0, 0);
    for (;;) {}
}

static void handle_enter_bootloader(const uint8_t *data, uint8_t dlc)
{
    /* Unlock key is mandatory — reject frames without it */
    if (dlc < 5) {
        send_response(CFG_CMD_ENTER_BOOTLOADER, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }
    uint32_t key = (uint32_t)data[1] |
                   ((uint32_t)data[2] << 8) |
                   ((uint32_t)data[3] << 16) |
                   ((uint32_t)data[4] << 24);
    if (key != RESET_UNLOCK_KEY) {
        send_response(CFG_CMD_ENTER_BOOTLOADER, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }
    send_response(CFG_CMD_ENTER_BOOTLOADER, CFG_STATUS_OK, NULL, 0);
    vTaskDelay(pdMS_TO_TICKS(50));
    hal_request_bootloader();
}

static void handle_read_param(const uint8_t *data, uint8_t dlc)
{
    if (dlc < 4) {
        send_response(CFG_CMD_READ_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }

    uint8_t section = data[1];
    uint8_t param   = data[2];
    uint8_t sub     = data[3];
    uint8_t payload[6] = {0};
    uint8_t plen = 0;

    /* Echo back address */
    payload[0] = section;
    payload[1] = param;
    payload[2] = sub;

    switch (section) {
    case CFG_SECTION_CAN:
        if (sub > 1) { send_response(CFG_CMD_READ_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0); return; }
        switch (param) {
        case 0: /* bitrate (4 bytes, LE) */
        {
            uint32_t br = s_working_config.can[sub].bitrate;
            payload[3] = (uint8_t)(br);
            payload[4] = (uint8_t)(br >> 8);
            payload[5] = (uint8_t)(br >> 16);
            plen = 6;
            break;
        }
        case 1: /* termination */
            payload[3] = s_working_config.can[sub].termination;
            plen = 4;
            break;
        case 2: /* enabled */
            payload[3] = s_working_config.can[sub].enabled;
            plen = 4;
            break;
        default:
            send_response(CFG_CMD_READ_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        break;

    case CFG_SECTION_LIN:
        if (sub >= LIN_CHANNEL_COUNT) { send_response(CFG_CMD_READ_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0); return; }
        switch (param) {
        case 0: /* enabled */
            payload[3] = s_working_config.lin[sub].enabled;
            plen = 4;
            break;
        case 1: /* mode */
            payload[3] = s_working_config.lin[sub].mode;
            plen = 4;
            break;
        case 2: /* baudrate */
        {
            uint32_t br = s_working_config.lin[sub].baudrate;
            payload[3] = (uint8_t)(br);
            payload[4] = (uint8_t)(br >> 8);
            payload[5] = (uint8_t)(br >> 16);
            plen = 6;
            break;
        }
        default:
            send_response(CFG_CMD_READ_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        break;

    case CFG_SECTION_DIAG:
        switch (param) {
        case 0: /* can_id */
        {
            uint32_t id = s_working_config.diag.can_id;
            payload[3] = (uint8_t)(id);
            payload[4] = (uint8_t)(id >> 8);
            payload[5] = (uint8_t)(id >> 16);
            plen = 6;
            break;
        }
        case 1: /* interval_ms */
        {
            uint16_t iv = s_working_config.diag.interval_ms;
            payload[3] = (uint8_t)(iv);
            payload[4] = (uint8_t)(iv >> 8);
            plen = 5;
            break;
        }
        case 2: /* enabled */
            payload[3] = s_working_config.diag.enabled;
            plen = 4;
            break;
        case 3: /* bus */
            payload[3] = s_working_config.diag.bus;
            plen = 4;
            break;
        case 4: /* can_watchdog_ms */
        {
            uint16_t wdt = s_working_config.diag.can_watchdog_ms;
            payload[3] = (uint8_t)(wdt);
            payload[4] = (uint8_t)(wdt >> 8);
            plen = 5;
            break;
        }
        case 5: /* lin_watchdog_ms */
        {
            uint16_t wdt = s_working_config.diag.lin_watchdog_ms;
            payload[3] = (uint8_t)(wdt);
            payload[4] = (uint8_t)(wdt >> 8);
            plen = 5;
            break;
        }
        case 6: /* bulk_tx_retries */
            payload[3] = s_working_config.diag.bulk_tx_retries;
            plen = 4;
            break;
        case 7: /* bulk_tx_retry_delay_ms */
            payload[3] = s_working_config.diag.bulk_tx_retry_delay_ms;
            plen = 4;
            break;
        default:
            send_response(CFG_CMD_READ_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        break;

    case CFG_SECTION_PROFILES:
        switch (param) {
        case 0: /* wda_enabled */
            payload[3] = s_working_config.profiles.wda_enabled;
            plen = 4;
            break;
        case 1: /* cwa400_enabled */
            payload[3] = s_working_config.profiles.cwa400_enabled;
            plen = 4;
            break;
        default:
            send_response(CFG_CMD_READ_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        break;

    case CFG_SECTION_DEVICE:
        switch (param) {
        case 0: /* sizeof(routing_rule_t) */
        {
            uint16_t sz = (uint16_t)sizeof(routing_rule_t);
            payload[3] = (uint8_t)(sz);
            payload[4] = (uint8_t)(sz >> 8);
            plen = 5;
            break;
        }
        case 1: /* sizeof(lin_schedule_entry_t) */
        {
            uint16_t sz = (uint16_t)sizeof(lin_schedule_entry_t);
            payload[3] = (uint8_t)(sz);
            payload[4] = (uint8_t)(sz >> 8);
            plen = 5;
            break;
        }
        case 2: /* sizeof(lin_schedule_table_t) */
        {
            uint16_t sz = (uint16_t)sizeof(lin_schedule_table_t);
            payload[3] = (uint8_t)(sz);
            payload[4] = (uint8_t)(sz >> 8);
            plen = 5;
            break;
        }
        default:
            send_response(CFG_CMD_READ_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        break;

    case CFG_SECTION_MONITOR:
        switch (param) {
        case MONITOR_PARAM_ENABLE:
            payload[3] = bus_monitor_get_enabled() ? 1 : 0;
            plen = 4;
            break;
        case MONITOR_PARAM_BUS_MASK:
            payload[3] = bus_monitor_get_bus_mask();
            plen = 4;
            break;
        case MONITOR_PARAM_FILTER_MODE:
            payload[3] = bus_monitor_get_filter_mode();
            plen = 4;
            break;
        case MONITOR_PARAM_DROP_COUNT:
            payload[3] = bus_monitor_get_drop_count();
            plen = 4;
            break;
        default:
            send_response(CFG_CMD_READ_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        break;

    case CFG_SECTION_LOG:
        switch (param) {
        case LOG_PARAM_MODE:
            payload[3] = flash_logger_get_mode();
            plen = 4;
            break;
        case LOG_PARAM_BUS_MASK:
            payload[3] = flash_logger_get_bus_mask();
            plen = 4;
            break;
        case LOG_PARAM_STATUS:
            payload[3] = flash_logger_get_state();
            plen = 4;
            break;
        case LOG_PARAM_ENTRY_COUNT: {
            uint32_t count = flash_logger_get_entry_count();
            if (sub == 0) {
                payload[3] = (uint8_t)(count);
                payload[4] = (uint8_t)(count >> 8);
            } else {
                payload[3] = (uint8_t)(count >> 16);
                payload[4] = (uint8_t)(count >> 24);
            }
            plen = 5;
            break;
        }
        case LOG_PARAM_WRAP_COUNT: {
            uint32_t wraps = flash_logger_get_wrap_count();
            if (sub == 0) {
                payload[3] = (uint8_t)(wraps);
                payload[4] = (uint8_t)(wraps >> 8);
            } else {
                payload[3] = (uint8_t)(wraps >> 16);
                payload[4] = (uint8_t)(wraps >> 24);
            }
            plen = 5;
            break;
        }
        case LOG_PARAM_WRITE_OFFSET: {
            uint32_t off = flash_logger_get_write_offset();
            if (sub == 0) {
                payload[3] = (uint8_t)(off);
                payload[4] = (uint8_t)(off >> 8);
            } else {
                payload[3] = (uint8_t)(off >> 16);
                payload[4] = (uint8_t)(off >> 24);
            }
            plen = 5;
            break;
        }
        case LOG_PARAM_FLASH_ERRORS: {
            uint16_t errors = flash_logger_get_flash_errors();
            payload[3] = (uint8_t)(errors);
            payload[4] = (uint8_t)(errors >> 8);
            plen = 5;
            break;
        }
        case LOG_PARAM_DROP_COUNT: {
            uint32_t drops = flash_logger_get_drop_count();
            if (sub == 0) {
                payload[3] = (uint8_t)(drops);
                payload[4] = (uint8_t)(drops >> 8);
            } else {
                payload[3] = (uint8_t)(drops >> 16);
                payload[4] = (uint8_t)(drops >> 24);
            }
            plen = 5;
            break;
        }
        case LOG_PARAM_TRIGGER_BUS:
            payload[3] = flash_logger_get_trigger_bus();
            plen = 4;
            break;
        case LOG_PARAM_TRIGGER_ID: {
            uint32_t tid = flash_logger_get_trigger_id();
            if (sub == 0) {
                payload[3] = (uint8_t)(tid);
                payload[4] = (uint8_t)(tid >> 8);
            } else {
                payload[3] = (uint8_t)(tid >> 16);
                payload[4] = (uint8_t)(tid >> 24);
            }
            plen = 5;
            break;
        }
        case LOG_PARAM_TRIGGER_BYTE:
            payload[3] = flash_logger_get_trigger_byte();
            plen = 4;
            break;
        case LOG_PARAM_TRIGGER_OP:
            payload[3] = flash_logger_get_trigger_op();
            plen = 4;
            break;
        case LOG_PARAM_TRIGGER_VALUE:
            payload[3] = flash_logger_get_trigger_value();
            plen = 4;
            break;
        case LOG_PARAM_PRE_TRIG_KB: {
            uint16_t pre = flash_logger_get_pre_trigger_kb();
            payload[3] = (uint8_t)(pre);
            payload[4] = (uint8_t)(pre >> 8);
            plen = 5;
            break;
        }
        case LOG_PARAM_POST_TRIG_KB: {
            uint16_t post = flash_logger_get_post_trigger_kb();
            payload[3] = (uint8_t)(post);
            payload[4] = (uint8_t)(post >> 8);
            plen = 5;
            break;
        }
        default:
            send_response(CFG_CMD_READ_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        break;

    default:
        send_response(CFG_CMD_READ_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }

    send_response(CFG_CMD_READ_PARAM, CFG_STATUS_OK, payload, plen);
}

static void handle_write_param(const uint8_t *data, uint8_t dlc)
{
    if (dlc < 5) {
        send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }

    uint8_t section = data[1];
    uint8_t param   = data[2];
    uint8_t sub     = data[3];
    /* Value bytes start at data[4] */

    switch (section) {
    case CFG_SECTION_CAN:
        if (sub > 1) { send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0); return; }
        switch (param) {
        case 0: /* bitrate (LE, up to 3 bytes in frame) */ {
            if (dlc < 7) { send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0); return; }
            uint32_t br = (uint32_t)data[4] |
                          ((uint32_t)data[5] << 8) |
                          ((uint32_t)data[6] << 16);
            if (br < 10000 || br > 1000000) { send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0); return; }
            config_handler_lock();
            s_working_config.can[sub].bitrate = br;
            config_handler_unlock();
            break;
        }
        case 1: /* termination */
            s_working_config.can[sub].termination = data[4] ? 1 : 0;
            break;
        case 2: /* enabled */
            if (sub == 0 && data[4] == 0) {
                /* Prevent disabling CAN1 — it carries the config protocol */
                send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
                return;
            }
            s_working_config.can[sub].enabled = data[4] ? 1 : 0;
            break;
        default:
            send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        break;

    case CFG_SECTION_LIN:
        if (sub >= LIN_CHANNEL_COUNT) { send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0); return; }
        switch (param) {
        case 0: /* enabled */
            s_working_config.lin[sub].enabled = data[4] ? 1 : 0;
            break;
        case 1: /* mode */
            s_working_config.lin[sub].mode = data[4];
            break;
        case 2: /* baudrate */ {
            if (dlc < 7) { send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0); return; }
            uint32_t lbr = (uint32_t)data[4] |
                           ((uint32_t)data[5] << 8) |
                           ((uint32_t)data[6] << 16);
            if (lbr < 1000 || lbr > 20000) { send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0); return; }
            config_handler_lock();
            s_working_config.lin[sub].baudrate = lbr;
            config_handler_unlock();
            break;
        }
        default:
            send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        break;

    case CFG_SECTION_DIAG:
        switch (param) {
        case 0: /* can_id */ {
            if (dlc < 7) { send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0); return; }
            uint32_t new_id = (uint32_t)data[4] |
                              ((uint32_t)data[5] << 8) |
                              ((uint32_t)data[6] << 16);
            if (new_id > 0x7FF) { send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0); return; }
            config_handler_lock();
            s_working_config.diag.can_id = new_id;
            config_handler_unlock();
        }
            break;
        case 1: /* interval_ms */
            if (dlc < 6) { send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0); return; }
            config_handler_lock();
            s_working_config.diag.interval_ms = (uint16_t)data[4] | ((uint16_t)data[5] << 8);
            config_handler_unlock();
            break;
        case 2: /* enabled */
            s_working_config.diag.enabled = data[4] ? 1 : 0;
            break;
        case 3: /* bus */
            s_working_config.diag.bus = data[4];
            break;
        case 4: /* can_watchdog_ms */
            if (dlc < 6) { send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0); return; }
            config_handler_lock();
            s_working_config.diag.can_watchdog_ms = (uint16_t)data[4] | ((uint16_t)data[5] << 8);
            config_handler_unlock();
            break;
        case 5: /* lin_watchdog_ms */
            if (dlc < 6) { send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0); return; }
            config_handler_lock();
            s_working_config.diag.lin_watchdog_ms = (uint16_t)data[4] | ((uint16_t)data[5] << 8);
            config_handler_unlock();
            break;
        case 6: /* bulk_tx_retries */
            s_working_config.diag.bulk_tx_retries = data[4];
            break;
        case 7: /* bulk_tx_retry_delay_ms */
            s_working_config.diag.bulk_tx_retry_delay_ms = data[4];
            break;
        default:
            send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        break;

    case CFG_SECTION_PROFILES:
        switch (param) {
        case 0: /* wda_enabled + channel */
            s_working_config.profiles.wda_enabled = data[4] ? 1 : 0;
            if (dlc >= 6) s_working_config.profiles.wda_channel = data[5];
            break;
        case 1: /* cwa400_enabled + channel */
            s_working_config.profiles.cwa400_enabled = data[4] ? 1 : 0;
            if (dlc >= 6) s_working_config.profiles.cwa400_channel = data[5];
            break;
        default:
            send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        break;

    case CFG_SECTION_MONITOR:
        if (dlc < 5) {
            send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        switch (param) {
        case MONITOR_PARAM_ENABLE:
            bus_monitor_set_enabled(data[4] != 0);
            break;
        case MONITOR_PARAM_BUS_MASK:
            bus_monitor_set_bus_mask(data[4]);
            break;
        case MONITOR_PARAM_FILTER_MODE:
            bus_monitor_set_filter_mode(data[4]);
            break;
        default:
            send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_OK, NULL, 0);
        return;

    case CFG_SECTION_LOG:
        if (dlc < 5) {
            send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        switch (param) {
        case LOG_PARAM_MODE:
            flash_logger_set_mode(data[4]);
            break;
        case LOG_PARAM_BUS_MASK:
            flash_logger_set_bus_mask(data[4]);
            break;
        case LOG_PARAM_STATE_CMD:
            if (data[4] == 1) {
                flash_logger_start();
            } else if (data[4] == 0) {
                flash_logger_stop();
            } else if (data[4] == 2) {
                flash_logger_arm();
            } else if (data[4] == 0xFF) {
                flash_logger_erase_all();
            }
            break;
        case LOG_PARAM_TRIGGER_BUS:
            flash_logger_set_trigger_bus(data[4]);
            break;
        case LOG_PARAM_TRIGGER_ID:
            if (dlc >= 8) {
                uint32_t tid = (uint32_t)data[4] | ((uint32_t)data[5] << 8)
                             | ((uint32_t)data[6] << 16) | ((uint32_t)data[7] << 24);
                flash_logger_set_trigger_id(tid);
            }
            break;
        case LOG_PARAM_TRIGGER_BYTE:
            flash_logger_set_trigger_byte(data[4]);
            break;
        case LOG_PARAM_TRIGGER_OP:
            flash_logger_set_trigger_op(data[4]);
            break;
        case LOG_PARAM_TRIGGER_VALUE:
            flash_logger_set_trigger_value(data[4]);
            break;
        case LOG_PARAM_PRE_TRIG_KB:
            if (dlc >= 6) {
                uint16_t kb = (uint16_t)data[4] | ((uint16_t)data[5] << 8);
                flash_logger_set_pre_trigger_kb(kb);
            }
            break;
        case LOG_PARAM_POST_TRIG_KB:
            if (dlc >= 6) {
                uint16_t kb = (uint16_t)data[4] | ((uint16_t)data[5] << 8);
                flash_logger_set_post_trigger_kb(kb);
            }
            break;
        default:
            send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_OK, NULL, 0);
        return;

    default:
        send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }

    send_response(CFG_CMD_WRITE_PARAM, CFG_STATUS_OK, NULL, 0);
}

/* ---- Bulk Transfer ---- */

static void handle_bulk_start(const uint8_t *data, uint8_t dlc)
{
    if (dlc < 8) {
        send_response(CFG_CMD_BULK_START, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }

    s_bulk_section       = data[1];
    s_bulk_sub           = data[2];
    s_bulk_expected_size = (uint16_t)data[3] | ((uint16_t)data[4] << 8);
    s_bulk_expected_crc  = (uint32_t)data[5] | ((uint32_t)data[6] << 8) |
                           ((uint32_t)data[7] << 16);  /* 24-bit CRC */

    /* Validate section + sub */
    if (s_bulk_section == CFG_SECTION_LIN && s_bulk_sub >= LIN_CHANNEL_COUNT) {
        send_response(CFG_CMD_BULK_START, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }

    if (s_bulk_expected_size > sizeof(s_bulk_buffer)) {
        send_response(CFG_CMD_BULK_START, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }

    if (s_bulk_section == CFG_SECTION_MONITOR &&
        s_bulk_expected_size > MONITOR_MAX_FILTER_IDS * sizeof(uint32_t)) {
        send_response(CFG_CMD_BULK_START, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }

    s_bulk_active   = true;
    s_bulk_received = 0;
    s_bulk_seq      = 0;
    memset(s_bulk_buffer, 0, sizeof(s_bulk_buffer));

    send_response(CFG_CMD_BULK_START, CFG_STATUS_OK, NULL, 0);
}

static void handle_bulk_data(const uint8_t *data, uint8_t dlc)
{
    if (!s_bulk_active || dlc < 2) return;

    uint8_t seq = data[0];
    if (seq != s_bulk_seq) {
        /* Sequence error — abort */
        s_bulk_active = false;
        return;
    }

    uint8_t payload_len = dlc - 1;
    uint16_t remaining = s_bulk_expected_size - s_bulk_received;
    if (payload_len > remaining) payload_len = (uint8_t)remaining;

    memcpy(&s_bulk_buffer[s_bulk_received], &data[1], payload_len);
    s_bulk_received += payload_len;
    s_bulk_seq++;
}

static void handle_bulk_end(const uint8_t *data, uint8_t dlc)
{
    (void)data;
    (void)dlc;

    if (!s_bulk_active) {
        send_response(CFG_CMD_BULK_END, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }

    s_bulk_active = false;

    /* Verify CRC of received data (24-bit comparison) */
    uint32_t actual_crc = crc32_compute(s_bulk_buffer, s_bulk_received) & 0x00FFFFFF;
    if (actual_crc != s_bulk_expected_crc) {
        send_response(CFG_CMD_BULK_END, CFG_STATUS_CRC_MISMATCH, NULL, 0);
        return;
    }

    /* Deserialize based on section */
    switch (s_bulk_section) {
    case CFG_SECTION_ROUTING: {
        uint8_t count = (uint8_t)(s_bulk_received / sizeof(routing_rule_t));
        if (count > MAX_ROUTING_RULES) count = MAX_ROUTING_RULES;
        config_handler_lock();
        s_working_config.routing_rule_count = count;
        memcpy(s_working_config.routing_rules, s_bulk_buffer,
               count * sizeof(routing_rule_t));
        config_handler_unlock();
        break;
    }
    case CFG_SECTION_LIN: {
        if (s_bulk_sub >= LIN_CHANNEL_COUNT) {
            send_response(CFG_CMD_BULK_END, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        if (s_bulk_received > sizeof(lin_schedule_table_t)) {
            send_response(CFG_CMD_BULK_END, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        /* Zero first to clear stale entries from previous config */
        config_handler_lock();
        memset(&s_working_config.lin[s_bulk_sub].schedule, 0,
               sizeof(lin_schedule_table_t));
        memcpy(&s_working_config.lin[s_bulk_sub].schedule, s_bulk_buffer,
               s_bulk_received);
        config_handler_unlock();
        break;
    }
    case CFG_SECTION_MONITOR:
    {
        /* Bulk data is an array of uint32_t IDs */
        uint8_t count = s_bulk_received / 4;
        if (count > MONITOR_MAX_FILTER_IDS)
            count = MONITOR_MAX_FILTER_IDS;
        bus_monitor_set_filter_ids((const uint32_t *)s_bulk_buffer, count);
        break;
    }
    default:
        send_response(CFG_CMD_BULK_END, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }

    send_response(CFG_CMD_BULK_END, CFG_STATUS_OK, NULL, 0);
}

/* ---- Bulk Read (two-step handshake) ---- */

/* Prepared size for BULK_READ_DATA to stream */
static uint16_t s_bulk_read_size;

static void handle_bulk_read(const uint8_t *data, uint8_t dlc)
{
    if (dlc < 3) {
        send_response(CFG_CMD_BULK_READ, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }

    uint8_t section = data[1];
    uint8_t sub     = data[2];
    uint16_t size   = 0;

    /* Serialize requested section into bulk buffer */
    switch (section) {
    case CFG_SECTION_ROUTING: {
        uint8_t count = s_working_config.routing_rule_count;
        if (count > MAX_ROUTING_RULES) count = MAX_ROUTING_RULES;
        size = (uint16_t)(count * sizeof(routing_rule_t));
        memcpy(s_bulk_buffer, s_working_config.routing_rules, size);
        break;
    }
    case CFG_SECTION_LIN: {
        if (sub >= LIN_CHANNEL_COUNT) {
            send_response(CFG_CMD_BULK_READ, CFG_STATUS_INVALID_PARAM, NULL, 0);
            return;
        }
        size = sizeof(lin_schedule_table_t);
        memcpy(s_bulk_buffer, &s_working_config.lin[sub].schedule, size);
        break;
    }
    default:
        send_response(CFG_CMD_BULK_READ, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }

    /* Stash size for BULK_READ_DATA; data stays in s_bulk_buffer */
    s_bulk_read_size = size;

    /* Compute CRC32 over serialized data */
    uint32_t crc = crc32_compute(s_bulk_buffer, size);

    /* Send response header: [cmd] [status] [size_lo] [size_hi] [crc_b0..b3] */
    uint8_t payload[6];
    payload[0] = (uint8_t)(size);
    payload[1] = (uint8_t)(size >> 8);
    payload[2] = (uint8_t)(crc);
    payload[3] = (uint8_t)(crc >> 8);
    payload[4] = (uint8_t)(crc >> 16);
    payload[5] = (uint8_t)(crc >> 24);
    send_response(CFG_CMD_BULK_READ, CFG_STATUS_OK, payload, 6);
}

static void handle_bulk_read_data(void)
{
    uint16_t size = s_bulk_read_size;
    if (size == 0) {
        send_response(CFG_CMD_BULK_READ_DATA, CFG_STATUS_OK, NULL, 0);
        return;
    }

    /* Read retry config (0 = use compile-time default) */
    uint32_t max_retries = s_working_config.diag.bulk_tx_retries;
    if (max_retries == 0) max_retries = CFG_BULK_TX_RETRIES;
    uint32_t retry_delay = s_working_config.diag.bulk_tx_retry_delay_ms;
    if (retry_delay == 0) retry_delay = CFG_BULK_TX_RETRY_DELAY_MS;

    send_response(CFG_CMD_BULK_READ_DATA, CFG_STATUS_OK, NULL, 0);

    /* Stream data frames on BULK_RESP_ID */
    uint16_t offset = 0;
    uint8_t  seq    = 0;
    while (offset < size) {
        can_frame_t frame = {0};
        frame.id  = CONFIG_CAN_BULK_RESP_ID;
        frame.data[0] = seq++;

        uint16_t remaining = size - offset;
        uint8_t  chunk     = (remaining > 7) ? 7 : (uint8_t)remaining;
        memcpy(&frame.data[1], &s_bulk_buffer[offset], chunk);
        frame.dlc = 1 + chunk;
        offset += chunk;

        /* Retry with yield if TX queue is full; abort on timeout */
        uint32_t retries = 0;
        while (!can_manager_transmit(CAN_BUS_1, &frame)) {
            if (++retries >= max_retries) {
                s_bulk_read_size = 0;
                return;  /* CAN bus fault — abort transfer */
            }
            vTaskDelay(pdMS_TO_TICKS(retry_delay));
        }
    }

    s_bulk_read_size = 0;
}

/* ---- Chunked Log Read (0x24) ---- */

static uint8_t s_log_chunk_buffer[NVM_SECTOR_SIZE];

static void handle_log_read_chunk(const uint8_t *data, uint8_t dlc)
{
    if (dlc < 6) {
        send_response(CFG_CMD_LOG_READ_CHUNK, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }

    uint32_t offset = (uint32_t)data[1]
                    | ((uint32_t)data[2] << 8)
                    | ((uint32_t)data[3] << 16);
    uint16_t length = (uint16_t)data[4] | ((uint16_t)data[5] << 8);

    if (length > NVM_SECTOR_SIZE) length = NVM_SECTOR_SIZE;

    uint16_t actual = flash_logger_read_chunk(offset, s_log_chunk_buffer, length);
    if (actual == 0) {
        send_response(CFG_CMD_LOG_READ_CHUNK, CFG_STATUS_INVALID_PARAM, NULL, 0);
        return;
    }

    uint32_t crc = crc32_compute(s_log_chunk_buffer, actual);

    can_frame_t tx_frame;
    tx_frame.id = CONFIG_CAN_BULK_RESP_ID;
    tx_frame.flags = 0;

    uint16_t sent = 0;
    uint8_t seq = 0;
    while (sent < actual) {
        uint8_t chunk = (actual - sent > 7) ? 7 : (uint8_t)(actual - sent);
        tx_frame.dlc = 1 + chunk;
        tx_frame.data[0] = seq++;
        memcpy(&tx_frame.data[1], &s_log_chunk_buffer[sent], chunk);
        can_manager_transmit(CAN_BUS_1, &tx_frame);
        sent += chunk;

        if ((seq & 0x0F) == 0)
            vTaskDelay(1);
    }

    uint8_t ack_payload[6];
    ack_payload[0] = (uint8_t)(actual);
    ack_payload[1] = (uint8_t)(actual >> 8);
    ack_payload[2] = (uint8_t)(crc);
    ack_payload[3] = (uint8_t)(crc >> 8);
    ack_payload[4] = (uint8_t)(crc >> 16);
    ack_payload[5] = (uint8_t)(crc >> 24);
    send_response(CFG_CMD_LOG_READ_CHUNK, CFG_STATUS_OK, ack_payload, 6);
}

/* ---- Command Dispatch ---- */

static void dispatch_command(const gateway_frame_t *gf)
{
    const uint8_t *data = gf->frame.data;
    uint8_t dlc = gf->frame.dlc;

    if (gf->frame.id == CONFIG_CAN_DATA_ID) {
        /* Bulk data frame (0x602) */
        handle_bulk_data(data, dlc);
        return;
    }

    /* Command frame (0x600) */
    if (dlc < 1) return;
    uint8_t cmd = data[0];

    switch (cmd) {
    case CFG_CMD_CONNECT:           handle_connect(); break;
    case CFG_CMD_GET_STATUS:        handle_get_status(); break;
    case CFG_CMD_SAVE:              handle_save(); break;
    case CFG_CMD_DEFAULTS:          handle_defaults(); break;
    case CFG_CMD_REBOOT:            handle_reboot(); break;
    case CFG_CMD_ENTER_BOOTLOADER:  handle_enter_bootloader(data, dlc); break;
    case CFG_CMD_READ_PARAM:        handle_read_param(data, dlc); break;
    case CFG_CMD_WRITE_PARAM:       handle_write_param(data, dlc); break;
    case CFG_CMD_BULK_START:        handle_bulk_start(data, dlc); break;
    case CFG_CMD_BULK_END:          handle_bulk_end(data, dlc); break;
    case CFG_CMD_BULK_READ:         handle_bulk_read(data, dlc); break;
    case CFG_CMD_BULK_READ_DATA:    handle_bulk_read_data(); break;
    case CFG_CMD_LOG_READ_CHUNK:    handle_log_read_chunk(data, dlc); break;
    default:
        send_response(cmd, CFG_STATUS_UNKNOWN_CMD, NULL, 0);
        break;
    }
}

/* ---- Public API ---- */

void config_handler_init(QueueHandle_t config_rx_queue, QueueHandle_t can_tx_queue)
{
    s_config_mutex = xSemaphoreCreateMutex();
    configASSERT(s_config_mutex);
    s_config_rx_queue = config_rx_queue;
    s_can_tx_queue    = can_tx_queue;
    s_bulk_active     = false;

    /* Load config from NVM (or defaults) */
    nvm_config_load(&s_working_config);
}

void config_handler_task(void *params)
{
    (void)params;

    for (;;) {
        gateway_frame_t gf;
        if (xQueueReceive(s_config_rx_queue, &gf, portMAX_DELAY) == pdTRUE) {
            dispatch_command(&gf);
        }
    }
}

const nvm_config_t *config_handler_get_config(void)
{
    return &s_working_config;
}

void config_handler_lock(void)
{
    xSemaphoreTake(s_config_mutex, portMAX_DELAY);
}

void config_handler_unlock(void)
{
    xSemaphoreGive(s_config_mutex);
}
