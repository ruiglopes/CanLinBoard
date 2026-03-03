#include "can/can_manager.h"
#include "board_config.h"
#include "hal/hal_gpio.h"

#include "can2040.h"
#include "hardware/irq.h"
#include "hardware/clocks.h"
#include "pico/time.h"

#include "FreeRTOS.h"
#include "task.h"
#include "queue.h"

/* PIO IRQ priority: must be ABOVE configMAX_SYSCALL_INTERRUPT_PRIORITY
 * (i.e. numerically lower) so FreeRTOS critical sections never mask it.
 * can2040 is timing-sensitive — deferring the PIO IRQ even briefly during
 * a FreeRTOS critical section causes lost RX frames.
 *
 * Consequence: NO FreeRTOS API calls allowed in can2040 callbacks.
 * Data passes through lock-free ring buffers; can_task polls every 1ms. */
#define CAN_PIO_IRQ_PRIORITY  0  /* Level 0 (highest), above BASEPRI */

#include <string.h>

/* ---- SPSC Lock-Free Ring Buffer ---- */
typedef struct {
    gateway_frame_t buf[CAN_RX_RING_SIZE];
    volatile uint32_t head;     /* Written by IRQ (producer) */
    volatile uint32_t tail;     /* Read by task (consumer) */
} can_ring_t;

static inline bool ring_push(can_ring_t *r, const gateway_frame_t *frame)
{
    uint32_t next = (r->head + 1) & (CAN_RX_RING_SIZE - 1);
    if (next == r->tail) return false;  /* Full */
    r->buf[r->head] = *frame;
    __dmb();
    r->head = next;
    return true;
}

static inline bool ring_pop(can_ring_t *r, gateway_frame_t *frame)
{
    if (r->head == r->tail) return false;  /* Empty */
    *frame = r->buf[r->tail];
    __dmb();
    r->tail = (r->tail + 1) & (CAN_RX_RING_SIZE - 1);
    return true;
}

/* ---- Module State ---- */
static struct can2040 can2040_inst[2];
static can_ring_t     can_rx_ring[2];
static can_bus_stats_t can_stats[2];

static QueueHandle_t  s_gateway_queue;
static QueueHandle_t  s_config_queue;
static QueueHandle_t  s_can_tx_queue;

static TaskHandle_t   s_can_task_handle;

/* ---- can2040 Callbacks (IRQ context) ---- */

static void can2040_to_can_frame(const struct can2040_msg *msg, can_frame_t *frame)
{
    frame->id = msg->id & 0x1FFFFFFF;
    frame->dlc = (msg->dlc > 8) ? 8 : msg->dlc;
    frame->flags = 0;
    if (msg->id & CAN2040_ID_RTR) frame->flags |= CAN_FLAG_RTR;
    if (msg->id & CAN2040_ID_EFF) frame->flags |= CAN_FLAG_EFF;
    memcpy(frame->data, msg->data, 8);
}

static void can1_callback(struct can2040 *cd, uint32_t notify, struct can2040_msg *msg)
{
    (void)cd;

    if (notify == CAN2040_NOTIFY_RX) {
        can_stats[0].isr_rx_count++;

        gateway_frame_t gf;
        gf.source_bus = BUS_CAN1;
        gf.timestamp = time_us_32() / 1000;  /* ms, ISR-safe hw timer */
        can2040_to_can_frame(msg, &gf.frame);

        if (!ring_push(&can_rx_ring[0], &gf)) {
            can_stats[0].ring_overflow_count++;
        }
        /* No FreeRTOS API here — IRQ is above BASEPRI.
         * can_task polls the ring buffer every 1ms. */

    } else if (notify == CAN2040_NOTIFY_TX) {
        can_stats[0].tx_count++;
    } else if (notify == CAN2040_NOTIFY_ERROR) {
        can_stats[0].error_count++;
    }
}

static void can2_callback(struct can2040 *cd, uint32_t notify, struct can2040_msg *msg)
{
    (void)cd;

    if (notify == CAN2040_NOTIFY_RX) {
        can_stats[1].isr_rx_count++;

        gateway_frame_t gf;
        gf.source_bus = BUS_CAN2;
        gf.timestamp = time_us_32() / 1000;
        can2040_to_can_frame(msg, &gf.frame);

        if (!ring_push(&can_rx_ring[1], &gf)) {
            can_stats[1].ring_overflow_count++;
        }

    } else if (notify == CAN2040_NOTIFY_TX) {
        can_stats[1].tx_count++;
    } else if (notify == CAN2040_NOTIFY_ERROR) {
        can_stats[1].error_count++;
    }
}

/* ---- PIO IRQ Handlers ---- */

static void pio0_irq_handler(void)
{
    can2040_pio_irq_handler(&can2040_inst[0]);
}

static void pio1_irq_handler(void)
{
    can2040_pio_irq_handler(&can2040_inst[1]);
}

/* ---- Public API ---- */

void can_manager_init(QueueHandle_t gateway_queue,
                      QueueHandle_t config_queue,
                      QueueHandle_t can_tx_queue)
{
    s_gateway_queue = gateway_queue;
    s_config_queue  = config_queue;
    s_can_tx_queue  = can_tx_queue;

    memset(can_rx_ring, 0, sizeof(can_rx_ring));
    memset(can_stats, 0, sizeof(can_stats));
    can_stats[0].state = CAN_STATE_UNINIT;
    can_stats[1].state = CAN_STATE_UNINIT;
}

bool can_manager_start_can1(uint32_t bitrate)
{
    /* Enable CAN1 transceiver */
    hal_can_enable(CAN_BUS_1, true);

    /* Setup can2040 on PIO0 */
    can2040_setup(&can2040_inst[0], CAN1_PIO_NUM);
    can2040_callback_config(&can2040_inst[0], can1_callback);

    /* PIO0 IRQ must be above FreeRTOS BASEPRI so it's never masked */
    irq_set_exclusive_handler(PIO0_IRQ_0, pio0_irq_handler);
    irq_set_priority(PIO0_IRQ_0, CAN_PIO_IRQ_PRIORITY);
    irq_set_enabled(PIO0_IRQ_0, true);

    /* Start CAN — use actual system clock for accurate bit timing */
    uint32_t sys_clock = clock_get_hz(clk_sys);
    can2040_start(&can2040_inst[0], sys_clock, bitrate, CAN1_RX_PIN, CAN1_TX_PIN);

    can_stats[0].state = CAN_STATE_ACTIVE;
    return true;
}

bool can_manager_start_can2(uint32_t bitrate)
{
    /* Enable CAN2 transceiver */
    hal_can_enable(CAN_BUS_2, true);

    /* Setup can2040 on PIO1 */
    can2040_setup(&can2040_inst[1], CAN2_PIO_NUM);
    can2040_callback_config(&can2040_inst[1], can2_callback);

    /* PIO1 IRQ must be above FreeRTOS BASEPRI so it's never masked */
    irq_set_exclusive_handler(PIO1_IRQ_0, pio1_irq_handler);
    irq_set_priority(PIO1_IRQ_0, CAN_PIO_IRQ_PRIORITY);
    irq_set_enabled(PIO1_IRQ_0, true);

    /* Start CAN — use actual system clock for accurate bit timing */
    uint32_t sys_clock2 = clock_get_hz(clk_sys);
    can2040_start(&can2040_inst[1], sys_clock2, bitrate, CAN2_RX_PIN, CAN2_TX_PIN);

    can_stats[1].state = CAN_STATE_ACTIVE;
    return true;
}

void can_manager_stop_can2(void)
{
    can2040_stop(&can2040_inst[1]);
    irq_set_enabled(PIO1_IRQ_0, false);
    hal_can_enable(CAN_BUS_2, false);
    can_stats[1].state = CAN_STATE_DISABLED;
}

bool can_manager_transmit(can_bus_id_t bus, const can_frame_t *frame)
{
    struct can2040 *inst = &can2040_inst[bus];
    uint pio_irq = (bus == CAN_BUS_1) ? PIO0_IRQ_0 : PIO1_IRQ_0;

    /* can2040_transmit must not be preempted by can2040_pio_irq_handler.
     * Since the PIO IRQ runs above BASEPRI (priority 0), FreeRTOS critical
     * sections don't mask it. Explicitly disable the IRQ around the call. */
    irq_set_enabled(pio_irq, false);

    bool ok = false;
    if (can2040_check_transmit(inst)) {
        struct can2040_msg msg;
        msg.id = frame->id;
        if (frame->flags & CAN_FLAG_RTR) msg.id |= CAN2040_ID_RTR;
        if (frame->flags & CAN_FLAG_EFF) msg.id |= CAN2040_ID_EFF;
        msg.dlc = frame->dlc;
        memcpy(msg.data, frame->data, 8);
        ok = (can2040_transmit(inst, &msg) == 0);
    }

    irq_set_enabled(pio_irq, true);
    return ok;
}

void can_manager_get_stats(can_bus_id_t bus, can_bus_stats_t *stats)
{
    *stats = can_stats[bus];
}

/* ---- CAN Task ---- */

static bool is_config_frame(const gateway_frame_t *gf)
{
    /* Config protocol frames are on CAN1 with specific IDs */
    if (gf->source_bus != BUS_CAN1) return false;
    uint32_t id = gf->frame.id;
    return (id == CONFIG_CAN_CMD_ID || id == CONFIG_CAN_DATA_ID);
}

static void check_bootloader_cmd(const gateway_frame_t *gf)
{
    /* Only accept reboot command on CAN1 */
    if (gf->source_bus != BUS_CAN1) return;
    if (gf->frame.id != BL_CAN_CMD_ID) return;
    if (gf->frame.dlc < 6) return;
    if (gf->frame.data[0] != CMD_RESET) return;
    if (gf->frame.data[1] != RESET_MODE_BOOTLOADER) return;

    /* Validate 4-byte unlock key (little-endian) */
    uint32_t key = (uint32_t)gf->frame.data[2]
                 | ((uint32_t)gf->frame.data[3] << 8)
                 | ((uint32_t)gf->frame.data[4] << 16)
                 | ((uint32_t)gf->frame.data[5] << 24);
    if (key != RESET_UNLOCK_KEY) return;

    hal_request_bootloader();  /* noreturn */
}

void can_task_entry(void *params)
{
    (void)params;

    s_can_task_handle = xTaskGetCurrentTaskHandle();

    for (;;) {
        /* Wait for notification from IRQ (with timeout for TX queue) */
        ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(1));

        /* Drain both CAN RX ring buffers */
        gateway_frame_t gf;

        while (ring_pop(&can_rx_ring[0], &gf)) {
            can_stats[0].rx_count++;

            /* Check for reboot-to-bootloader command (noreturn if matched) */
            check_bootloader_cmd(&gf);

            if (is_config_frame(&gf)) {
                xQueueSend(s_config_queue, &gf, 0);
            } else {
                xQueueSend(s_gateway_queue, &gf, 0);
            }
        }

        while (ring_pop(&can_rx_ring[1], &gf)) {
            can_stats[1].rx_count++;
            xQueueSend(s_gateway_queue, &gf, 0);
        }

        /* Process outbound CAN TX queue */
        gateway_frame_t tx_gf;
        while (xQueueReceive(s_can_tx_queue, &tx_gf, 0) == pdTRUE) {
            can_bus_id_t bus = (tx_gf.source_bus == BUS_CAN1) ? CAN_BUS_1 : CAN_BUS_2;
            can_manager_transmit(bus, &tx_gf.frame);
        }
    }
}
