#include "lf_reader_generic.h"
#include "lf_reader_data.h"

#include "bsp_delay.h"
#include "bsp_wdt.h"
#include "bsp_time.h"
#include "circular_buffer.h"
#include "lf_125khz_radio.h"
#include "lf_reader_data.h"
#include "protocols/protocols.h"

#define NRF_LOG_MODULE_NAME lfgeneric
#include "nrf_log.h"
#include "nrf_log_ctrl.h"
#include "nrf_log_default_backends.h"
NRF_LOG_MODULE_REGISTER();

/*
 * Circular buffer for SAADC samples.
 * Sized at 3 * 2048 (6144) because SAADC EasyDMA transfers ADC_BUF_SIZE = 2048
 * samples per callback invocation.
 * The main loop drains it as fast as possible into the output buffer.
 */
#define CIRCULAR_BUFFER_SIZE (6144)
static circular_buffer cb;
static volatile uint32_t m_cb_gen = 0;
static volatile uint32_t m_active_gen = 0;

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
            return;  /* buffer full — oldest samples dropped */
        }
    }
}

static void init_saadc_hw(void) {
    lf_125khz_radio_saadc_enable(saadc_cb);
}

static void uninit_saadc_hw(void) {
    lf_125khz_radio_saadc_disable();
}

bool raw_read_to_buffer(uint8_t *data, size_t maxlen, uint32_t timeout_ms, size_t *outlen) {
    *outlen = 0;

    cb_init(&cb, CIRCULAR_BUFFER_SIZE, sizeof(uint16_t));
    start_lf_125khz_radio();

    /* Wait for antenna to settle before capturing.
     * The LC circuit rings for ~400µs on field startup, then takes
     * another ~800µs to reach steady state. Skip 2ms to be safe. */
    bsp_delay_ms(2);

    m_active_gen = ++m_cb_gen;
    init_saadc_hw();

    autotimer *p_at = bsp_obtain_timer(0);
    while (NO_TIMEOUT_1MS(p_at, timeout_ms) && *outlen < maxlen) {
        uint16_t val = 0;
        while (cb_pop_front(&cb, &val) && *outlen < maxlen) {
            val = val >> 5;  /* 14-bit ADC → 9-bit, then >>5 gives 8-bit */
            data[*outlen] = val > 0xff ? 0xff : (uint8_t)val;
            ++(*outlen);
        }
        bsp_wdt_feed();  /* prevent watchdog reset during long captures */
    }

    bsp_return_timer(p_at);
    m_active_gen = 0;
    uninit_saadc_hw();
    stop_lf_125khz_radio();
    cb_free(&cb);

    return true;
}
