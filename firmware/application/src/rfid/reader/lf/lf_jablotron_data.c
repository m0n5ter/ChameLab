#include "lf_reader_data.h"

#include <string.h>

#include "bsp_delay.h"
#include "bsp_time.h"
#include "circular_buffer.h"
#include "lf_125khz_radio.h"
#include "lf_reader_data.h"
#include "protocols/jablotron.h"
#include "protocols/protocols.h"

#define NRF_LOG_MODULE_NAME jablotron_reader
#include "nrf_log.h"
#include "nrf_log_ctrl.h"
#include "nrf_log_default_backends.h"
NRF_LOG_MODULE_REGISTER();

#define JABLOTRON_BUFFER_SIZE (128)

static circular_buffer cb;

static void jablotron_gpio_int0_cb(void) {
    uint32_t ticks = lf_125khz_radio_edge_interval();
    uint16_t count = ticks > UINT16_MAX ? UINT16_MAX : (uint16_t)ticks;
    cb_push_back(&cb, &count);
}

static void init_jablotron_hw(void) {
    register_rio_callback(jablotron_gpio_int0_cb);
    lf_125khz_radio_gpiote_capture_enable();
}

static void uninit_jablotron_hw(void) {
    unregister_rio_callback();
    lf_125khz_radio_gpiote_disable();
}

bool jablotron_read(uint8_t *data, uint32_t timeout_ms) {
    void *codec = jablotron.alloc();
    jablotron.decoder.start(codec, 0);

    cb_init(&cb, JABLOTRON_BUFFER_SIZE, sizeof(uint16_t));
    init_jablotron_hw();
    start_lf_125khz_radio();

    uint8_t candidates[8][5];
    size_t candidate_count = 0;
    bool ok = false;
    autotimer *p_at = bsp_obtain_timer(0);
    while (!ok && NO_TIMEOUT_1MS(p_at, timeout_ms)) {
        uint16_t val = 0;
        while (!ok && NO_TIMEOUT_1MS(p_at, timeout_ms) && cb_pop_front(&cb, &val)) {
            if (jablotron.decoder.feed(codec, val)) {
                uint8_t *decoded = jablotron.get_data(codec);
                for (size_t i = 0; i < candidate_count; i++) {
                    if (memcmp(candidates[i], decoded, sizeof(candidates[i])) == 0) {
                        memcpy(data, decoded, jablotron.data_size);
                        ok = true;
                        break;
                    }
                }
                if (!ok && candidate_count < sizeof(candidates) / sizeof(candidates[0])) {
                    memcpy(candidates[candidate_count++], decoded, jablotron.data_size);
                }
            }
        }
    }

    bsp_return_timer(p_at);
    stop_lf_125khz_radio();
    uninit_jablotron_hw();
    cb_free(&cb);

    jablotron.free(codec);
    return ok;
}
