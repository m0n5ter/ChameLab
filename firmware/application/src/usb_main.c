#include "usb_main.h"

#include <string.h>
#include "syssleep.h"
#include "settings.h"
#include "dataframe.h"
#include "ble_main.h"

#include "app_usbd.h"
#include "app_usbd_cdc_acm.h"
#include "app_usbd_core.h"
#include "app_usbd_serial_num.h"
#include "app_usbd_string_desc.h"

#define NRF_LOG_MODULE_NAME usb_cdc
#include "nrf_log.h"
#include "nrf_log_ctrl.h"
#include "nrf_log_default_backends.h"
NRF_LOG_MODULE_REGISTER();

// USB DEFINES START
static void cdc_acm_user_ev_handler(app_usbd_class_inst_t const *p_inst, app_usbd_cdc_acm_user_event_t event);

#define CDC_ACM_COMM_INTERFACE 0
#define CDC_ACM_COMM_EPIN NRF_DRV_USBD_EPIN2

#define CDC_ACM_DATA_INTERFACE 1
#define CDC_ACM_DATA_EPIN NRF_DRV_USBD_EPIN1
#define CDC_ACM_DATA_EPOUT NRF_DRV_USBD_EPOUT1

/** @brief CDC_ACM class instance */
APP_USBD_CDC_ACM_GLOBAL_DEF(m_app_cdc_acm,
                            cdc_acm_user_ev_handler,
                            CDC_ACM_COMM_INTERFACE,
                            CDC_ACM_DATA_INTERFACE,
                            CDC_ACM_COMM_EPIN,
                            CDC_ACM_DATA_EPIN,
                            CDC_ACM_DATA_EPOUT,
                            APP_USBD_CDC_COMM_PROTOCOL_AT_V250);

// USB DEFINES END

// USB CODE START
volatile bool g_usb_connected = false;
volatile bool g_usb_port_opened = false;
volatile bool g_usb_led_marquee_enable = true;
static uint8_t cdc_data_buffer[NRF_DRV_USBD_EPSIZE];

// Replies wait here until the host collects them. The CDC driver sends straight from the
// caller's buffer and takes one transfer at a time: a reply made while the previous one was
// still going out used to overwrite it and then fail with NRF_ERROR_BUSY, which reset the
// device. A desktop OS reads a serial port continuously, so that rarely happened there; the
// Android USB Host API reads only when the app asks, and requests sent back to back hit it.
// Two full frames fit (4096 data bytes plus framing each).
#define USB_TX_QUEUE_SIZE 8448
static uint8_t m_tx_queue[USB_TX_QUEUE_SIZE];
static size_t m_tx_head;       // where the next reply goes
static size_t m_tx_tail;       // first byte not yet sent
static size_t m_tx_used;       // queued bytes, including the transfer in flight
static size_t m_tx_in_flight;  // bytes of the transfer the driver is sending

static void usb_tx_reset(void) {
    m_tx_head = m_tx_tail = m_tx_used = m_tx_in_flight = 0;
}

/** @brief Hands the driver the next contiguous run of queued bytes, unless a transfer is going out. */
static void usb_tx_kick(void) {
    if (m_tx_in_flight != 0 || m_tx_used == 0) return;
    size_t chunk = MIN(m_tx_used, USB_TX_QUEUE_SIZE - m_tx_tail);
    ret_code_t ret = app_usbd_cdc_acm_write(&m_app_cdc_acm, &m_tx_queue[m_tx_tail], chunk);
    if (ret == NRF_SUCCESS) {
        m_tx_in_flight = chunk;
    } else if (ret != NRF_ERROR_BUSY) {
        // The port went away (closed, unplugged): nobody is left to read the replies.
        NRF_LOG_WARNING("USB reply dropped, write failed: %d", ret);
        usb_tx_reset();
    }
    // NRF_ERROR_BUSY: a transfer we no longer track is finishing; its TX_DONE sends ours.
}

static void usb_port_set_open(bool open) {
    g_usb_port_opened = open;
    ble_usb_cdc_set_open(open);
}

/** @brief User event handler @ref app_usbd_cdc_acm_user_ev_handler_t */
static void cdc_acm_user_ev_handler(app_usbd_class_inst_t const *p_inst, app_usbd_cdc_acm_user_event_t event) {

    // app_usbd_cdc_acm_t const *p_cdc_acm = app_usbd_cdc_acm_class_get(p_inst);

    switch (event) {
        case APP_USBD_CDC_ACM_USER_EVT_PORT_OPEN: {
            // Setup first transfer
            ret_code_t ret = app_usbd_cdc_acm_read_any(&m_app_cdc_acm, cdc_data_buffer, sizeof(cdc_data_buffer));
            UNUSED_VARIABLE(ret);

            NRF_LOG_INFO("CDC ACM port opened");
            usb_tx_reset();
            usb_port_set_open(true);
            break;
        }

        case APP_USBD_CDC_ACM_USER_EVT_PORT_CLOSE:
            NRF_LOG_INFO("CDC ACM port closed");
            usb_port_set_open(false);
            usb_tx_reset();
            g_usb_led_marquee_enable = true;
            break;

        case APP_USBD_CDC_ACM_USER_EVT_TX_DONE:
            if (m_tx_in_flight != 0) {
                m_tx_tail = (m_tx_tail + m_tx_in_flight) % USB_TX_QUEUE_SIZE;
                m_tx_used -= m_tx_in_flight;
                m_tx_in_flight = 0;
            }
            usb_tx_kick();
            break;

        case APP_USBD_CDC_ACM_USER_EVT_RX_DONE: {
            // Get amount of data transfered to process data
            size_t size = app_usbd_cdc_acm_rx_size(&m_app_cdc_acm);
            data_frame_receive(cdc_data_buffer, size);

            // Setup next transfer
            ret_code_t ret = app_usbd_cdc_acm_read_any(&m_app_cdc_acm, cdc_data_buffer, sizeof(cdc_data_buffer));
            UNUSED_VARIABLE(ret);
            break;
        }
        default:
            break;
    }
}

static void usbd_user_ev_handler(app_usbd_event_type_t event) {
    switch (event) {
        case APP_USBD_EVT_DRV_SUSPEND:
            NRF_LOG_INFO("USB SUSPEND");
            break;

        case APP_USBD_EVT_DRV_RESUME:
            NRF_LOG_INFO("USB RESUME");
            break;

        case APP_USBD_EVT_STARTED:
            NRF_LOG_INFO("USB STARTED");
            break;

        case APP_USBD_EVT_STOPPED:
            NRF_LOG_INFO("USB STOPPED");
            usb_port_set_open(false);
            usb_tx_reset();
            app_usbd_disable();
            break;

        case APP_USBD_EVT_POWER_DETECTED:
            sleep_timer_stop();
            NRF_LOG_INFO("USB power detected");
            if (!nrf_drv_usbd_is_enabled()) {
                app_usbd_enable();
            }
            g_usb_led_marquee_enable = true;
            break;

        case APP_USBD_EVT_POWER_REMOVED:
            sleep_timer_start(settings_get_sleep_timeout());
            NRF_LOG_INFO("USB power removed");
            g_usb_connected = false;
            usb_port_set_open(false);
            usb_tx_reset();
            g_usb_led_marquee_enable = false;
            app_usbd_stop();
            break;

        case APP_USBD_EVT_POWER_READY:
            NRF_LOG_INFO("USB ready");
            g_usb_connected = true;
            app_usbd_start();
            break;

        default:
            // NRF_LOG_INFO("Other usb event: %d", event);
            break;
    }
}

// USB CODE END

void usb_cdc_init(void) {
    ret_code_t ret;
    static const app_usbd_config_t usbd_config = {
        .ev_state_proc = usbd_user_ev_handler
    };

    app_usbd_serial_num_generate();

    ret = app_usbd_init(&usbd_config);
    APP_ERROR_CHECK(ret);

    app_usbd_class_inst_t const *class_cdc_acm = app_usbd_cdc_acm_class_inst_get(&m_app_cdc_acm);
    ret = app_usbd_class_append(class_cdc_acm);
    APP_ERROR_CHECK(ret);
}

/**
 * @brief Queues a reply for the host; it goes out once the replies before it have been read.
 * Main loop context only, like the USB event processing that sends the queue on.
 */
void usb_cdc_write(const void *p_buf, uint16_t length) {
    if (length > USB_TX_QUEUE_SIZE - m_tx_used) {
        // The host has stopped reading: losing this reply beats resetting the device.
        NRF_LOG_WARNING("USB reply dropped, %d bytes still unread", m_tx_used);
        return;
    }
    const uint8_t *src = p_buf;
    size_t first = MIN((size_t)length, USB_TX_QUEUE_SIZE - m_tx_head);
    memcpy(&m_tx_queue[m_tx_head], src, first);
    memcpy(m_tx_queue, src + first, length - first);
    m_tx_head = (m_tx_head + length) % USB_TX_QUEUE_SIZE;
    m_tx_used += length;
    usb_tx_kick();
}

// override fputc to printf to cdc serial
/* dont't enable
int fputc(int ch, FILE *f){
    static int ch_static;
    ch_static = ch;

    // must cdc is available
    if (g_usb_port_opened && g_usb_connected) {
        // send and wait done.
        ret_code_t ret;
        do {
            ret = app_usbd_cdc_acm_write(&m_app_cdc_acm, &ch_static, 1);
        } while(ret == NRF_ERROR_BUSY);

        // error log
        if (ret != NRF_SUCCESS) {
            NRF_LOG_ERROR("CDC ACM Unavailable, fputc: %c, return code: %d", ch_static, ret);
        }
    }

    return ch;
}
*/

bool is_usb_working(void) {
    return g_usb_port_opened;
}
