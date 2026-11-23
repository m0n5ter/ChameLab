#include <string.h>

#include "bsp_delay.h"
#include "bsp_time.h"
#include "circular_buffer.h"
#include "lf_125khz_radio.h"
#include "lf_reader_data.h"
#include "nrfx_saadc.h"
#include "protocols/pac.h"
#include "protocols/protocols.h"

#define NRF_LOG_MODULE_NAME pac_reader
#include "nrf_log.h"
#include "nrf_log_ctrl.h"
#include "nrf_log_default_backends.h"
NRF_LOG_MODULE_REGISTER();

#define PAC_BUFFER_SIZE (6144)

static circular_buffer cb;
static volatile uint32_t m_cb_gen = 0;
static volatile uint32_t m_active_gen = 0;

// SAADC callback — push raw ADC samples to circular buffer.
// NRZ/Direct modulation requires ADC sampling (not GPIOTE edge timing)
// because the comparator may not produce clean digital edges for NRZ signals.
static void pac_saadc_cb(nrf_saadc_value_t *vals, size_t size) {
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

static void init_pac_hw(void) {
    lf_125khz_radio_saadc_enable(pac_saadc_cb);
}

static void uninit_pac_hw(void) {
    lf_125khz_radio_saadc_disable();
}

bool pac_read(uint8_t *data, uint32_t timeout_ms) {
    void *codec = pac.alloc();
    void *wide = pac.alloc();
    if (!codec || !wide) {
        pac.free(codec);
        pac.free(wide);
        return false;
    }
    pac.decoder.start(codec, 0);
    pac.decoder.start(wide, PAC_DECODE_WIDE_HYSTERESIS);

    if (!cb_init(&cb, PAC_BUFFER_SIZE, sizeof(uint16_t))) {
        pac.free(codec);
        pac.free(wide);
        return false;
    }

    // Back-to-back reads must restart the tag, rather than calibrating on an
    // arbitrary continuation of its previous frame while its supply decays.
    stop_lf_125khz_radio();
    lf_125khz_radio_saadc_disable();
    bsp_delay_ms(50);

    // Start carrier first, then wait for T55XX POR (~5ms) before
    // enabling SAADC.  This ensures the prescan calibration phase
    // sees real NRZ signal levels rather than power-up noise.
    start_lf_125khz_radio();
    bsp_delay_ms(10);

    m_active_gen = ++m_cb_gen;
    init_pac_hw();

    bool ok = false;
    autotimer *p_at = bsp_obtain_timer(0);
    while (!ok && NO_TIMEOUT_1MS(p_at, timeout_ms)) {
        uint16_t val = 0;
        while (!ok && NO_TIMEOUT_1MS(p_at, timeout_ms) && cb_pop_front(&cb, &val)) {
            if (pac.decoder.feed(codec, val)) {
                memcpy(data, pac.get_data(codec), pac.data_size);
                ok = true;
                break;
            }
            // AC coupling returns a long NRZ level toward the baseline. A
            // narrow dead zone can count that recovery as another data edge;
            // retain the original path and check a wider hysteresis in parallel.
            if (pac.decoder.feed(wide, val)) {
                memcpy(data, pac.get_data(wide), pac.data_size);
                ok = true;
                break;
            }
        }
    }

    bsp_return_timer(p_at);
    m_active_gen = 0;
    uninit_pac_hw();
    stop_lf_125khz_radio();
    cb_free(&cb);

    pac.free(codec);
    pac.free(wide);
    return ok;
}
