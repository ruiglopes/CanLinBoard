#include "diag/diagnostics.h"
#include "diag/fault_handler.h"
#include "diag/bus_watchdog.h"
#include "board_config.h"
#include "can/can_bus.h"
#include "can/can_manager.h"
#include "lin/lin_manager.h"
#include "gateway/gateway_engine.h"
#include "config/config_handler.h"
#include "config/nvm_config.h"
#include "app_header.h"

#include "hardware/adc.h"
#include "FreeRTOS.h"
#include "task.h"

/* ---- Module State ---- */
static TaskHandle_t *s_task_handles;
static uint8_t       s_num_tasks;
static volatile system_state_t s_sys_state = SYS_STATE_BOOT;
static reset_reason_t s_reset_reason = RESET_POWER_ON;

/* ---- MCU Temperature Reading ---- */

static int8_t read_mcu_temp(void)
{
    adc_select_input(ADC_TEMPERATURE_CHANNEL_NUM);
    uint16_t raw = adc_read();
    float voltage = raw * 3.3f / 4096.0f;
    float temp_c = 27.0f - (voltage - 0.706f) / 0.001721f;
    return (int8_t)temp_c;
}

/* ---- System State Update ---- */

static void update_system_state(void)
{
    /* Bus watchdog timeout → ERROR (highest priority) */
    if (bus_watchdog_get_timeout_mask() != 0) {
        s_sys_state = SYS_STATE_ERROR;
        return;
    }

    /* Stack watermark low → WARN */
    for (int i = 0; i < s_num_tasks; i++) {
        if (s_task_handles[i]) {
            UBaseType_t wm = uxTaskGetStackHighWaterMark(s_task_handles[i]);
            if (wm < 32) {
                s_sys_state = SYS_STATE_WARN;
                return;
            }
        }
    }

    s_sys_state = SYS_STATE_OK;
}

/* ---- Heartbeat Helpers ---- */

static uint8_t get_bus_active_mask(void)
{
    uint8_t bus_mask = 0;
    can_bus_stats_t cs;

    can_manager_get_stats(CAN_BUS_1, &cs);
    if (cs.state == CAN_STATE_ACTIVE) bus_mask |= 0x01;

    can_manager_get_stats(CAN_BUS_2, &cs);
    if (cs.state == CAN_STATE_ACTIVE) bus_mask |= 0x02;

    for (uint8_t ch = 0; ch < LIN_CHANNEL_COUNT; ch++) {
        lin_channel_stats_t ls;
        lin_manager_get_stats(ch, &ls);
        if (ls.state == LIN_STATE_ACTIVE) bus_mask |= (0x04 << ch);
    }
    return bus_mask;
}

static void send_heartbeat(uint32_t base_id, can_bus_id_t tx_bus, uint32_t uptime_s)
{
    /* ---- Frame 1: System status (8B) ---- */
    {
        can_frame_t hb = {0};
        hb.id = base_id;
        hb.dlc = 8;

        hb.data[0] = (uint8_t)(uptime_s >> 24);
        hb.data[1] = (uint8_t)(uptime_s >> 16);
        hb.data[2] = (uint8_t)(uptime_s >> 8);
        hb.data[3] = (uint8_t)(uptime_s);
        hb.data[4] = (uint8_t)s_sys_state;
        hb.data[5] = get_bus_active_mask();
        hb.data[6] = (uint8_t)read_mcu_temp();
        hb.data[7] = (uint8_t)s_reset_reason;

        can_manager_transmit(tx_bus, &hb);
    }

    vTaskDelay(pdMS_TO_TICKS(5));

    /* ---- Frame 2: CAN + Gateway stats (8B) ---- */
    {
        can_bus_stats_t can1_stats, can2_stats;
        can_manager_get_stats(CAN_BUS_1, &can1_stats);
        can_manager_get_stats(CAN_BUS_2, &can2_stats);

        gateway_stats_t gw_stats;
        gateway_engine_get_stats(&gw_stats);

        can_frame_t sf = {0};
        sf.id = base_id + 1;
        sf.dlc = 8;

        sf.data[0] = (uint8_t)(can1_stats.rx_count >> 8);
        sf.data[1] = (uint8_t)(can1_stats.rx_count);
        sf.data[2] = (can1_stats.error_count > 255) ? 255 : (uint8_t)can1_stats.error_count;
        sf.data[3] = (uint8_t)(can2_stats.rx_count >> 8);
        sf.data[4] = (uint8_t)(can2_stats.rx_count);
        sf.data[5] = (can2_stats.error_count > 255) ? 255 : (uint8_t)can2_stats.error_count;
        sf.data[6] = (uint8_t)(gw_stats.frames_routed >> 8);
        sf.data[7] = (uint8_t)(gw_stats.frames_routed);

        can_manager_transmit(tx_bus, &sf);
    }

    vTaskDelay(pdMS_TO_TICKS(5));

    /* ---- Frame 3: LIN stats (8B) ---- */
    {
        can_frame_t lf = {0};
        lf.id = base_id + 2;
        lf.dlc = 8;

        for (uint8_t ch = 0; ch < LIN_CHANNEL_COUNT; ch++) {
            lin_channel_stats_t ls;
            lin_manager_get_stats(ch, &ls);
            lf.data[ch * 2]     = (ls.rx_count > 255) ? 255 : (uint8_t)ls.rx_count;
            lf.data[ch * 2 + 1] = (ls.error_count > 255) ? 255 : (uint8_t)ls.error_count;
        }

        can_manager_transmit(tx_bus, &lf);
    }

    vTaskDelay(pdMS_TO_TICKS(5));

    /* ---- Frame 4: System health (3B) ---- */
    {
        can_frame_t hf = {0};
        hf.id = base_id + 4;
        hf.dlc = 3;

        size_t free_heap = xPortGetFreeHeapSize();
        hf.data[0] = (uint8_t)(free_heap / 1024);

        uint16_t min_wm = 0xFFFF;
        for (int i = 0; i < s_num_tasks; i++) {
            if (s_task_handles[i]) {
                UBaseType_t wm = uxTaskGetStackHighWaterMark(s_task_handles[i]);
                if (wm < min_wm) min_wm = (uint16_t)wm;
            }
        }
        hf.data[1] = (min_wm > 255) ? 255 : (uint8_t)min_wm;
        hf.data[2] = bus_watchdog_get_timeout_mask();

        can_manager_transmit(tx_bus, &hf);
    }
}

/* ---- Public API ---- */

void diagnostics_init(TaskHandle_t *task_handles, uint8_t num_tasks)
{
    s_task_handles = task_handles;
    s_num_tasks = num_tasks;
}

void diagnostics_set_reset_reason(reset_reason_t reason)
{
    s_reset_reason = reason;
}

system_state_t diagnostics_get_state(void)
{
    return s_sys_state;
}

void diagnostics_reconfigure(void)
{
    const nvm_config_t *cfg = config_handler_get_config();
    bus_watchdog_reconfigure(cfg->diag.can_watchdog_ms, cfg->diag.lin_watchdog_ms);
}

void diagnostics_task(void *params)
{
    (void)params;

    /* Wait for subsystems to initialize */
    vTaskDelay(pdMS_TO_TICKS(500));

    const nvm_config_t *cfg = config_handler_get_config();

    /* Copy startup config under lock */
    config_handler_lock();
    uint16_t init_can_wd_ms = cfg->diag.can_watchdog_ms;
    uint16_t init_lin_wd_ms = cfg->diag.lin_watchdog_ms;
    uint32_t init_can_id    = cfg->diag.can_id;
    uint8_t  init_bus       = cfg->diag.bus;
    config_handler_unlock();

    /* Initialize bus watchdogs with NVM config timeouts */
    bus_watchdog_init(init_can_wd_ms, init_lin_wd_ms);

    /* Enable watchdogs for active buses */
    {
        can_bus_stats_t cs;
        can_manager_get_stats(CAN_BUS_1, &cs);
        if (cs.state == CAN_STATE_ACTIVE)
            bus_watchdog_set_enabled(BUS_CAN1, true);

        can_manager_get_stats(CAN_BUS_2, &cs);
        if (cs.state == CAN_STATE_ACTIVE)
            bus_watchdog_set_enabled(BUS_CAN2, true);

        for (uint8_t ch = 0; ch < LIN_CHANNEL_COUNT; ch++) {
            lin_channel_stats_t ls;
            lin_manager_get_stats(ch, &ls);
            if (ls.state == LIN_STATE_ACTIVE)
                bus_watchdog_set_enabled((bus_id_t)(BUS_LIN1 + ch), true);
        }
    }

    /* One-shot: version frame */
    {
        can_bus_id_t tx_bus = (init_bus == 0) ? CAN_BUS_1 : CAN_BUS_2;
        can_frame_t diag = {0};
        diag.id = init_can_id;
        diag.dlc = 6;
        diag.data[0] = FW_VERSION_MAJOR;
        diag.data[1] = FW_VERSION_MINOR;
        diag.data[2] = FW_VERSION_PATCH;
        diag.data[3] = 0xAA;  /* "app running" sentinel */
#ifndef NO_BOOTLOADER
        diag.data[4] = (uint8_t)(app_header.crc32 >> 8);
        diag.data[5] = (uint8_t)(app_header.crc32);
#endif
        can_manager_transmit(tx_bus, &diag);
    }

    /* One-shot: crash report */
    {
        const crash_data_t *cd = fault_handler_get_crash_data();
        if (cd->magic == CRASH_DATA_MAGIC) {
            can_bus_id_t tx_bus = (init_bus == 0) ? CAN_BUS_1 : CAN_BUS_2;
            can_frame_t crash = {0};
            crash.id = init_can_id + 3;
            crash.dlc = 8;
            crash.data[0] = (uint8_t)cd->fault_type;
            crash.data[1] = (uint8_t)(cd->pc >> 24);
            crash.data[2] = (uint8_t)(cd->pc >> 16);
            crash.data[3] = (uint8_t)(cd->pc >> 8);
            crash.data[4] = (uint8_t)(cd->pc);
            uint16_t crash_uptime_s = (uint16_t)(cd->uptime_ms / 1000);
            crash.data[5] = (uint8_t)(crash_uptime_s >> 8);
            crash.data[6] = (uint8_t)(crash_uptime_s);
            crash.data[7] = (uint8_t)cd->task_name[0];
            can_manager_transmit(tx_bus, &crash);

            fault_handler_clear();
        }
    }

    s_sys_state = SYS_STATE_OK;
    uint32_t uptime_s = 0;
    uint32_t state_check_counter = 0;
    TickType_t last_wake = xTaskGetTickCount();

    for (;;) {
        /* Copy diag config under lock */
        config_handler_lock();
        cfg = config_handler_get_config();
        uint32_t hb_can_id   = cfg->diag.can_id;
        uint16_t hb_interval = cfg->diag.interval_ms;
        uint8_t  hb_bus      = cfg->diag.bus;
        bool     hb_enabled  = cfg->diag.enabled;
        config_handler_unlock();

        if (!hb_enabled || hb_interval == 0) {
            vTaskDelay(pdMS_TO_TICKS(1000));
            last_wake = xTaskGetTickCount(); /* re-sync after disabled period */
            state_check_counter++;
            if (state_check_counter >= 10) {
                state_check_counter = 0;
                update_system_state();
            }
            uptime_s++;
            continue;
        }

        vTaskDelayUntil(&last_wake, pdMS_TO_TICKS(hb_interval));
        uptime_s += (hb_interval + 500) / 1000;  /* approximate */

        can_bus_id_t tx_bus = (hb_bus == 0) ? CAN_BUS_1 : CAN_BUS_2;
        send_heartbeat(hb_can_id, tx_bus, uptime_s);

        state_check_counter++;
        if (state_check_counter >= 10) {
            state_check_counter = 0;
            update_system_state();
        }
    }
}
