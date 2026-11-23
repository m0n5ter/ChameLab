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

static bool electra_sample_transition(electra_sample_filter *filter, unsigned sample, unsigned median_win) {
    unsigned bit = (electra_samples[sample / 8] >> (7 - sample % 8)) & 1;
    // Median filter removes narrow comparator spikes while retaining half-bit pulses.
    filter->ones += bit;
    filter->ones -= (filter->history >> (median_win - 1)) & 1;
    filter->history = ((filter->history << 1) | bit) & ((1u << median_win) - 1);
    bool level = filter->ones >= (median_win + 1) / 2;
    if (level == filter->level) return false;
    filter->level = level;
    return true;
}

static bool electra_dma_decode_protocol(const protocol *p, void *codec, unsigned half_bit, unsigned median_win,
                                        uint8_t *data, uint8_t (*candidates)[13], size_t *candidate_count) {
    p->decoder.start(codec, 0);
    // Transitions lie on a half_bit-carrier-cycle grid. Recover phase from the
    // whole capture, then vote over each half-bit.
    unsigned phases[32] = {0};
    electra_sample_filter filter = {0};
    for (unsigned sample = 0; sample < sizeof(electra_samples) * 8; sample++) {
        if (electra_sample_transition(&filter, sample, median_win) && sample >= 512)
            phases[sample % half_bit]++;
    }
    unsigned phase = 0;
    for (unsigned i = 1; i < half_bit; i++) {
        if (phases[i] > phases[phase]) phase = i;
    }
    // The causal median filter delays transitions by (median_win - 1) / 2 samples.
    phase = (phase + half_bit - median_win / 2) % half_bit;
    if (p->tag_type == TAG_TYPE_EM410X_ELECTRA) {
        em410x_capture_diagnostics[4] = phase;
    }

    unsigned last_edge = 0;
    bool have_edge = false;
    bool previous = false;
    for (unsigned edge = phase; edge + half_bit <= sizeof(electra_samples) * 8; edge += half_bit) {
        unsigned ones = 0;
        for (unsigned sample = edge; sample < edge + half_bit; sample++)
            ones += (electra_samples[sample / 8] >> (7 - sample % 8)) & 1;
        bool level = (ones * 2 == half_bit) ? previous : (ones * 2 > half_bit);
        if (level && !previous) {
            if (have_edge) {
                unsigned ticks = edge - last_edge;
                uint16_t interval = ticks > 0xff ? 0xff : ticks;
                if (p->decoder.feed(codec, interval)) {
                    uint8_t *decoded = p->get_data(codec);
                    if (p->tag_type == TAG_TYPE_EM410X_ELECTRA) {
                        em410x_capture_diagnostics[2]++;
                        for (size_t i = 0; i < *candidate_count; i++) {
                            if (memcmp(candidates[i], decoded, sizeof(candidates[i])) == 0) {
                                data[0] = p->tag_type >> 8;
                                data[1] = p->tag_type;
                                memcpy(data + 2, decoded, p->data_size);
                                em410x_capture_diagnostics[7] = 1;
                                return true;
                            }
                        }
                        if (*candidate_count < 8)
                            memcpy(candidates[(*candidate_count)++], decoded, p->data_size);
                        em410x_capture_diagnostics[3] = *candidate_count;
                    } else {
                        data[0] = p->tag_type >> 8;
                        data[1] = p->tag_type;
                        memcpy(data + 2, decoded, p->data_size);
                        return true;
                    }
                }
            }
            last_edge = edge;
            have_edge = true;
        }
        previous = level;
    }
    return false;
}

static bool electra_dma_read(void **codecs, uint8_t *data) {
    memset(em410x_capture_diagnostics, 0, sizeof(em410x_capture_diagnostics));
    em410x_capture_diagnostics[0] = 0xEC310001;
    uint8_t candidates[8][13];
    size_t candidate_count = 0;

    static const struct {
        tag_specific_type_t tag_type;
        unsigned half_bit;
        unsigned median_win;
    } dma_cfgs[] = {
        { TAG_TYPE_EM410X_ELECTRA, 32, 21 },
        { TAG_TYPE_EM410X_64,      32, 21 },
        { TAG_TYPE_EM410X_32,      16, 11 },
        { TAG_TYPE_EM410X_16,       8,  5 },
    };

    // Electra's extension has no parity. Require two identical full frames,
    // so rejecting a comparator spike cannot silently change an extension bit.
    for (unsigned attempt = 0; attempt < 2; attempt++) {
        em410x_capture_diagnostics[1]++;
        if (!lf_125khz_radio_capture_bits(electra_samples, sizeof(electra_samples), 350)) {
            em410x_capture_diagnostics[5] = 1;
            return false;
        }
        for (size_t j = 0; j < em410x_protocols_size; j++) {
            if (em410x_protocols[j]->tag_type == TAG_TYPE_EM410X_ELECTRA) {
                if (electra_dma_decode_protocol(em410x_protocols[j], codecs[j],
                                                dma_cfgs[0].half_bit, dma_cfgs[0].median_win,
                                                data, candidates, &candidate_count)) {
                    return true;
                }
                break;
            }
        }
        // If we already saw an Electra candidate on attempt 0, allow attempt 1
        // to confirm it before falling back to standard 64-bit EM410x protocols.
        if (candidate_count > 0 && attempt == 0) {
            continue;
        }
        for (size_t c = 1; c < sizeof(dma_cfgs) / sizeof(dma_cfgs[0]); c++) {
            for (size_t j = 0; j < em410x_protocols_size; j++) {
                if (em410x_protocols[j]->tag_type == dma_cfgs[c].tag_type) {
                    if (electra_dma_decode_protocol(em410x_protocols[j], codecs[j],
                                                    dma_cfgs[c].half_bit, dma_cfgs[c].median_win,
                                                    data, candidates, &candidate_count)) {
                        return true;
                    }
                    break;
                }
            }
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
        ok = electra_dma_read(codecs, data);
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
