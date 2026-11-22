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
// 262 ms at 125 kbit/s: four full Electra frames, independent of BLE ISR load.
static uint8_t electra_samples[4096];
uint32_t em410x_capture_diagnostics[8];

bool em410x_copy_last_capture(uint8_t *data, size_t offset, size_t length) {
    if (offset > sizeof(electra_samples) || length > sizeof(electra_samples) - offset) return false;
    memcpy(data, electra_samples + offset, length);
    return true;
}

typedef struct {
    bool level;
    unsigned history;
    unsigned ones;
} electra_sample_filter;

static bool electra_sample_transition(electra_sample_filter *filter, unsigned sample) {
    unsigned bit = (electra_samples[sample / 8] >> (7 - sample % 8)) & 1;
    // A 21-sample median removes spikes up to 10 samples wide while retaining
    // a real 32-sample half-bit, including one with a spike in its middle.
    filter->ones += bit;
    filter->ones -= (filter->history >> 20) & 1;
    filter->history = ((filter->history << 1) | bit) & 0x1fffff;
    bool level = filter->ones >= 11;
    if (level == filter->level) return false;
    filter->level = level;
    return true;
}

static bool electra_dma_read(void *codec, uint8_t *data) {
    memset(em410x_capture_diagnostics, 0, sizeof(em410x_capture_diagnostics));
    em410x_capture_diagnostics[0] = 0xEC310001;
    uint8_t candidates[8][13];
    size_t candidate_count = 0;
    // Electra's extension has no parity. Require two identical full frames,
    // so rejecting a comparator spike cannot silently change an extension bit.
    for (unsigned attempt = 0; attempt < 2; attempt++) {
        em410x_capture_diagnostics[1]++;
        if (!lf_125khz_radio_capture_bits(electra_samples, sizeof(electra_samples), 350)) {
            em410x_capture_diagnostics[5] = 1;
            return false;
        }
        em410x_electra.decoder.start(codec, 0);
        // RF/64 transitions lie on a 32-carrier-cycle half-bit grid. Recover
        // its phase from the whole capture, then vote over each half-bit.
        // This retains the bit even if a spike splits its comparator pulse.
        unsigned phases[32] = {0};
        electra_sample_filter filter = {0};
        for (unsigned sample = 0; sample < sizeof(electra_samples) * 8; sample++) {
            if (electra_sample_transition(&filter, sample) && sample >= 512)
                phases[sample % 32]++;
        }
        unsigned phase = 0;
        for (unsigned i = 1; i < 32; i++) {
            if (phases[i] > phases[phase]) phase = i;
        }
        // The causal 21-sample median delays transitions by 10 samples.
        phase = (phase + 22) % 32;
        em410x_capture_diagnostics[4] = phase;
        unsigned last_edge = 0;
        bool have_edge = false;
        bool previous = false;
        for (unsigned edge = phase; edge + 32 <= sizeof(electra_samples) * 8; edge += 32) {
            unsigned ones = 0;
            for (unsigned sample = edge; sample < edge + 32; sample++)
                ones += (electra_samples[sample / 8] >> (7 - sample % 8)) & 1;
            bool level = ones == 16 ? previous : ones > 16;
            if (level && !previous) {
                if (have_edge) {
                    unsigned ticks = edge - last_edge;
                    uint16_t interval = ticks > 0xff ? 0xff : ticks;
                    if (em410x_electra.decoder.feed(codec, interval)) {
                        em410x_capture_diagnostics[2]++;
                        uint8_t *decoded = em410x_electra.get_data(codec);
                        for (size_t i = 0; i < candidate_count; i++) {
                            if (memcmp(candidates[i], decoded, sizeof(candidates[i])) == 0) {
                                data[0] = em410x_electra.tag_type >> 8;
                                data[1] = em410x_electra.tag_type;
                                memcpy(data + 2, decoded, em410x_electra.data_size);
                                em410x_capture_diagnostics[7] = 1;
                                return true;
                            }
                        }
                        if (candidate_count < sizeof(candidates) / sizeof(candidates[0]))
                            memcpy(candidates[candidate_count++], decoded, em410x_electra.data_size);
                        em410x_capture_diagnostics[3] = candidate_count;
                    }
                }
                last_edge = edge;
                have_edge = true;
            }
            previous = level;
        }
    }
    return false;
}

static void capture_interval_cb(void) {
    uint32_t ticks = lf_125khz_radio_edge_interval();
    uint16_t interval = ticks > UINT16_MAX ? UINT16_MAX : (uint16_t)ticks;
    cb_push_back(&cb, &interval);
}

// Falling-edge intervals in carrier cycles. Unlike ADC samples, this fits
// several complete Electra frames into one USB response without decimation.
bool em410x_capture_intervals(uint16_t *data, size_t capacity, uint32_t timeout_ms, size_t *count) {
    *count = 0;
    if (!cb_init(&cb, EM410X_BUFFER_SIZE, sizeof(uint16_t))) return false;
    register_rio_callback(capture_interval_cb);
    lf_125khz_radio_gpiote_capture_enable();
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
    uint32_t cntr = lf_125khz_radio_edge_interval();
    uint16_t val = 0;
    if (cntr > 0xff) {
        val = 0xff;
    } else {
        val = cntr & 0xff;
    }
    cb_push_back(&cb, &val);
}

static void init_em410x_hw(void) {
    register_rio_callback(gpio_int0_cb);
    lf_125khz_radio_gpiote_capture_enable();
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
    bool have_electra = false;
    uint8_t electra_data[13];
    uint8_t em410x_data[2 + 13] = {0};
    uint32_t em410x_deadline = 0;
    // Once an EM410x frame is in, keep listening until the Electra deadline even
    // past timeout_ms, so a short timeout (the LF auto scan) still tells them apart.
#define EM410X_LISTENING() ((have_em410x || have_electra) ? p_at->time < em410x_deadline : NO_TIMEOUT_1MS(p_at, timeout_ms))
    autotimer *p_at = bsp_obtain_timer(0);
    while (!ok && EM410X_LISTENING()) {
        uint16_t val = 0;
        while (!ok && EM410X_LISTENING() && cb_pop_front(&cb, &val)) {
            for (size_t i = 0; i < em410x_protocols_size; i++) {
                const protocol *p = em410x_protocols[i];
                if (!p->decoder.feed(codecs[i], val)) {
                    continue;
                }
                if (p->tag_type == TAG_TYPE_EM410X_ELECTRA) {
                    uint8_t *decoded = p->get_data(codecs[i]);
                    if (have_electra && memcmp(electra_data, decoded, sizeof(electra_data)) == 0) {
                        data[0] = p->tag_type >> 8;
                        data[1] = p->tag_type;
                        memcpy(data + 2, decoded, p->data_size);
                        ok = true;
                        break;
                    }
                    if (!have_electra) {
                        em410x_deadline = p_at->time + EM410X_ELECTRA_WAIT_MS;
                        have_electra = true;
                    }
                    memcpy(electra_data, decoded, sizeof(electra_data));
                    continue;
                }
                if (!have_em410x) {
                    em410x_data[0] = p->tag_type >> 8;
                    em410x_data[1] = p->tag_type;
                    memcpy(em410x_data + 2, p->get_data(codecs[i]), p->data_size);
                    em410x_deadline = p_at->time + EM410X_ELECTRA_WAIT_MS;
                    have_em410x = true;
                }
            }
        }
    }
#undef EM410X_LISTENING

    bsp_return_timer(p_at);
    uninit_em410x_hw();
    if (!ok) {
        // A short frame may be Electra with its extension interrupted by BLE.
        // Confirm with an uninterrupted DMA capture before returning EM410x.
        ok = electra_dma_read(codecs[0], data);
        if (!ok && have_em410x) {
            memcpy(data, em410x_data, 2 + 5);
            ok = true;
        }
    }
    stop_lf_125khz_radio();
    cb_free(&cb);

    for (size_t i = 0; i < em410x_protocols_size; i++) {
        em410x_protocols[i]->free(codecs[i]);
    }
    free(codecs);
    return ok;
}
