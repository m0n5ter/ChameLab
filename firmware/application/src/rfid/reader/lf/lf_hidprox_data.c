#include <stdio.h>

#include "bsp_delay.h"
#include "bsp_time.h"
#include "circular_buffer.h"
#include "lf_125khz_radio.h"
#include "lf_reader_data.h"
#include "lf_reader_main.h"
#include "nrfx_saadc.h"
#include "protocols/hidprox.h"
#include "time.h"
#include "rfid_main.h"

#define NRF_LOG_MODULE_NAME lf_read
#include "nrf_log.h"
#include "nrf_log_ctrl.h"
#include "nrf_log_default_backends.h"
NRF_LOG_MODULE_REGISTER();

#define HIDPROX_BUFFER_SIZE (6144)

static circular_buffer cb;
static volatile uint32_t m_cb_gen = 0;
static volatile uint32_t m_active_gen = 0;

// saadc irq is used to sample ANT GPIO.
static void saadc_cb(nrf_saadc_value_t *vals, size_t size) {
    uint32_t gen = m_active_gen;
    if (gen == 0 || gen != m_cb_gen) {
        return;
    }

    for (size_t i = 0; i < size; i++) {
        if (m_active_gen != gen) {
            return;
        }
        nrf_saadc_value_t val = vals[i];
        if (!cb_push_back(&cb, &val)) {
            return;
        }
    }
}

static void init_hidprox_hw(void) {
    lf_125khz_radio_saadc_enable(saadc_cb);
}

static void uninit_hidprox_hw(void) {
    lf_125khz_radio_saadc_disable();
}

bool hidprox_read(uint8_t *data, uint8_t format_hint, uint32_t timeout_ms) {
    void *codec = hidprox.alloc();
    hidprox.decoder.start(codec, format_hint);

    cb_init(&cb, HIDPROX_BUFFER_SIZE, sizeof(uint16_t));
    start_lf_125khz_radio();
    bsp_delay_ms(2);
    m_active_gen = ++m_cb_gen;
    init_hidprox_hw();

    bool ok = false;
    autotimer *p_at = bsp_obtain_timer(0);
    while (!ok && NO_TIMEOUT_1MS(p_at, timeout_ms)) {
        uint16_t val = 0;
        while (!ok && NO_TIMEOUT_1MS(p_at, timeout_ms) && cb_pop_front(&cb, &val)) {
            if (hidprox.decoder.feed(codec, val)) {
                memcpy(data, hidprox.get_data(codec), hidprox.data_size);
                ok = true;
                break;
            }
        }
    }

    bsp_return_timer(p_at);
    m_active_gen = 0;
    uninit_hidprox_hw();
    stop_lf_125khz_radio();
    cb_free(&cb);

    hidprox.free(codec);
    return ok;
}
