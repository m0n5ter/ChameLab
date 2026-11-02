#include "lf_reader_data.h"

#include <stdlib.h>
#include <string.h>

#include "bsp_delay.h"
#include "bsp_time.h"
#include "circular_buffer.h"
#include "lf_125khz_radio.h"
#include "lf_reader_data.h"
#include "protocols/em410x.h"
#include "protocols/protocols.h"

#define NRF_LOG_MODULE_NAME em410x
#include "nrf_log.h"
#include "nrf_log_ctrl.h"
#include "nrf_log_default_backends.h"
NRF_LOG_MODULE_REGISTER();

#define EM410X_BUFFER_SIZE (128)
// Electra starts with a valid 64-bit EM410x frame. Keep receiving long enough
// for its following 64 bits (33 ms at RF/64). Allow two full 128-bit cycles
// when a noisy acquisition needs to resynchronize first.
#define EM410X_ELECTRA_WAIT_MS (160)

static circular_buffer cb;

static void capture_interval_cb(void) {
    uint32_t ticks = get_lf_counter_value();
    uint16_t interval = ticks > UINT16_MAX ? UINT16_MAX : (uint16_t)ticks;
    cb_push_back(&cb, &interval);
    clear_lf_counter_value();
}

// Falling-edge intervals in carrier cycles. Unlike ADC samples, this fits
// several complete Electra frames into one USB response without decimation.
bool em410x_capture_intervals(uint16_t *data, size_t capacity, uint32_t timeout_ms, size_t *count) {
    *count = 0;
    if (!cb_init(&cb, EM410X_BUFFER_SIZE, sizeof(uint16_t))) return false;
    register_rio_callback(capture_interval_cb);
    lf_125khz_radio_gpiote_enable();
    clear_lf_counter_value();
    start_lf_125khz_radio();
    autotimer *timer = bsp_obtain_timer(0);
    while (*count < capacity && NO_TIMEOUT_1MS(timer, timeout_ms)) {
        uint16_t interval;
        while (*count < capacity && cb_pop_front(&cb, &interval)) data[(*count)++] = interval;
    }
    bsp_return_timer(timer);
    stop_lf_125khz_radio();
    lf_125khz_radio_gpiote_disable();
    unregister_rio_callback();
    cb_free(&cb);
    return *count != 0;
}

// GPIO interrupt recovery function is used to detect the descending edge
void gpio_int0_cb(void) {
    uint32_t cntr = get_lf_counter_value();
    uint16_t val = 0;
    if (cntr > 0xff) {
        val = 0xff;
    } else {
        val = cntr & 0xff;
    }
    cb_push_back(&cb, &val);
    clear_lf_counter_value();
}

static void init_em410x_hw(void) {
    register_rio_callback(gpio_int0_cb);
    lf_125khz_radio_gpiote_enable();
}

static void uninit_em410x_hw(void) {
    lf_125khz_radio_gpiote_disable();
    unregister_rio_callback();
}

bool em410x_read(uint8_t *data, uint32_t timeout_ms) {
    void **codecs = malloc(em410x_protocols_size * sizeof(void *));
    for (size_t i = 0; i < em410x_protocols_size; i++) {
        codecs[i] = em410x_protocols[i]->alloc();
        em410x_protocols[i]->decoder.start(codecs[i], 0);
    }

    cb_init(&cb, EM410X_BUFFER_SIZE, sizeof(uint16_t));
    init_em410x_hw();
    start_lf_125khz_radio();

    bool ok = false;
    bool have_em410x = false;
    uint8_t em410x_data[2 + 13] = {0};
    uint32_t em410x_deadline = 0;
    autotimer *p_at = bsp_obtain_timer(0);
    while (!ok && NO_TIMEOUT_1MS(p_at, timeout_ms)) {
        if (have_em410x && p_at->time >= em410x_deadline) {
            break;
        }
        uint16_t val = 0;
        while (!ok && NO_TIMEOUT_1MS(p_at, timeout_ms) && cb_pop_front(&cb, &val)) {
            for (size_t i = 0; i < em410x_protocols_size; i++) {
                const protocol *p = em410x_protocols[i];
                if (!p->decoder.feed(codecs[i], val)) {
                    continue;
                }
                if (p->tag_type == TAG_TYPE_EM410X_ELECTRA) {
                    data[0] = p->tag_type >> 8;
                    data[1] = p->tag_type;
                    memcpy(data + 2, p->get_data(codecs[i]), p->data_size);
                    ok = true;
                    break;
                }
                if (!have_em410x) {
                    em410x_data[0] = p->tag_type >> 8;
                    em410x_data[1] = p->tag_type;
                    memcpy(em410x_data + 2, p->get_data(codecs[i]), p->data_size);
                    em410x_deadline = p_at->time + EM410X_ELECTRA_WAIT_MS;
                    have_em410x = true;
                }
            }
            if (have_em410x && p_at->time >= em410x_deadline) break;
        }
    }

    if (!ok && have_em410x) {
        memcpy(data, em410x_data, 2 + 5);
        ok = true;
    }

    bsp_return_timer(p_at);
    stop_lf_125khz_radio();
    uninit_em410x_hw();
    cb_free(&cb);

    for (size_t i = 0; i < em410x_protocols_size; i++) {
        em410x_protocols[i]->free(codecs[i]);
    }
    free(codecs);
    return ok;
}
