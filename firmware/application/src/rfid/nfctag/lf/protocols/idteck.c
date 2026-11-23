#include "idteck.h"

#include <stdlib.h>
#include <string.h>

#include "nordic_common.h"
#include "nrf_pwm.h"
#include "protocols.h"
#include "t55xx.h"
#include "tag_base_type.h"
#include "utils/psk1.h"

// IDTECK: 64-bit PSK1 frame at RF/32. The 32-bit fixed preamble 0x4944544B
// ("IDTK") occupies the first four bytes of the frame; the remaining four
// bytes are the card payload (a one-byte checksum followed by a 24-bit card
// number in a byte-reversed layout, matching the format used by common
// IDTECK readers; see cmdlfidteck.c in the Proxmark3 client for details).

#define IDTECK_PWM_ENTRIES        (IDTECK_BIT_COUNT * LF_PSK1_RF32_SUBCYCLES_PER_BIT)
#define IDTECK_T55XX_BLOCK_COUNT  (3)   // config word + 2 data blocks

static nrf_pwm_values_wave_form_t m_idteck_pwm_seq_vals[IDTECK_PWM_ENTRIES] = {};

static nrf_pwm_sequence_t m_idteck_pwm_seq = {
    .values.p_wave_form = m_idteck_pwm_seq_vals,
    .length = NRF_PWM_VALUES_LENGTH(m_idteck_pwm_seq_vals),
    .repeats = 0,
    .end_delay = 0,
};

static idteck_codec *idteck_alloc(void) {
    return calloc(1, sizeof(idteck_codec));
}

static void idteck_free(idteck_codec *d) {
    free(d);
}

static uint8_t *idteck_get_data(idteck_codec *d) {
    return d->data;
}

// PSK1 uses an RF/2 subcarrier: at one ADC sample per carrier cycle its phase
// is the sign of sum((-1)^n * sample[n]). A rolling RF/32 window cancels DC;
// all window phases tolerate acquisition anywhere within a bit. Preserve
// signed ADC samples here: unlike the FSK readers, this is a phase correlation.
static void idteck_decoder_start(idteck_codec *d, uint8_t format) {
    (void)format;
    memset(d, 0, sizeof(*d));
}

static bool idteck_select_frame(idteck_codec *d) {
    uint32_t strongest = 0, best_score = 0, best_peak = 0;
    uint64_t best_frame = 0;
    for (unsigned i = 0; i < IDTECK_PHASES; i++) {
        const idteck_lane *lane = &d->lanes[i];
        if (!lane->have_previous || lane->bits_seen - lane->previous_at > 2) continue;
        if (lane->confidence > strongest) strongest = lane->confidence;
        if (!lane->confirmed) continue;
        unsigned votes = 0;
        uint32_t score = 0, peak = 0;
        for (unsigned j = 0; j < IDTECK_PHASES; j++) {
            const idteck_lane *other = &d->lanes[j];
            if (!other->confirmed || other->previous != lane->previous ||
                    other->bits_seen - other->previous_at > 2) continue;
            votes++;
            score += other->confidence;
            if (other->confidence > peak) peak = other->confidence;
        }
        if (votes >= 3 && score > best_score) {
            best_score = score;
            best_peak = peak;
            best_frame = lane->previous;
        }
    }
    // A half-bit window can repeat the same wrong payload with a good
    // preamble. Require agreement and a phase near the strongest signal,
    // even if the stronger (changing/noisy) candidates are not confirmed.
    if (!best_score || best_peak * 4 < strongest * 3) return false;
    for (unsigned i = 0; i < IDTECK_DATA_SIZE; i++)
        d->data[i] = (uint8_t)(best_frame >> (56 - i * 8));
    return true;
}

static bool idteck_decoder_feed(idteck_codec *d, uint16_t val) {
    if (d->settle && --d->settle == 0 && idteck_select_frame(d)) return true;
    int16_t sample = (int16_t)val;
    int32_t delta = (int32_t)sample - d->samples[d->position];
    d->samples[d->position] = sample;
    d->correlation += (d->position & 1) ? -delta : delta;
    d->position = (d->position + 1) % IDTECK_RF_PER_BIT;
    if (d->filled < IDTECK_RF_PER_BIT) d->filled++;
    if (++d->step < IDTECK_RF_PER_BIT / IDTECK_PHASES) return false;
    d->step = 0;
    idteck_lane *lane = &d->lanes[d->lane];
    d->lane = (d->lane + 1) % IDTECK_PHASES;
    if (d->filled < IDTECK_RF_PER_BIT) return false;

    lane->bits = (lane->bits << 1) | (d->correlation > 0);
    lane->bits_seen++;
    lane->energy += d->correlation < 0 ? -d->correlation : d->correlation;
    if (lane->count < IDTECK_BIT_COUNT) lane->count++;
    if (lane->count < IDTECK_BIT_COUNT) return false;

    uint64_t frame = lane->bits;
    if ((uint32_t)(frame >> 32) == (uint32_t)~IDTECK_PREAMBLE) frame = ~frame;
    if ((uint32_t)(frame >> 32) != IDTECK_PREAMBLE) return false;
    // Payload checksum variants are not established; require two identical
    // complete frames instead of rejecting genuine cards using a guessed CRC.
    uint32_t span = lane->bits_seen - lane->previous_at;
    lane->confidence = (lane->energy - lane->previous_energy) / span;
    lane->confirmed = lane->have_previous && lane->previous == frame && span >= IDTECK_BIT_COUNT;
    lane->previous = frame;
    lane->have_previous = true;
    lane->previous_at = lane->bits_seen;
    lane->previous_energy = lane->energy;
    // Wait one complete bit so every phase has had a chance to contribute;
    // do not return the first phase whose repeated preamble happens to pass.
    if (lane->confirmed && !d->settle) d->settle = IDTECK_RF_PER_BIT;
    return false;
}

// buf is the 8-byte frame to transmit, MSB first on air. For standard IDTECK
// the first four bytes are the fixed preamble and the last four are the card
// payload; the CLI layer is responsible for composing them.
static const nrf_pwm_sequence_t *idteck_modulator(idteck_codec *d, uint8_t *buf) {
    (void)d;

    size_t n = lf_psk1_build_sequence(buf, IDTECK_BIT_COUNT,
                                      m_idteck_pwm_seq_vals, IDTECK_PWM_ENTRIES);
    m_idteck_pwm_seq.length = (uint16_t)(n * 4);   // 4 uint16 fields per wave-form entry
    return &m_idteck_pwm_seq;
}

const protocol idteck = {
    .tag_type = TAG_TYPE_IDTECK,
    .data_size = IDTECK_DATA_SIZE,
    .alloc = (codec_alloc)idteck_alloc,
    .free = (codec_free)idteck_free,
    .get_data = (codec_get_data)idteck_get_data,
    .modulator = (modulator)idteck_modulator,
    .decoder =
        {
            .start = (decoder_start)idteck_decoder_start,
            .feed = (decoder_feed)idteck_decoder_feed,
        },
};

// T5577 writer: block 0 holds the PSK1 RF/32 configuration, blocks 1-2 hold
// the 64-bit frame big-endian.
uint8_t idteck_t55xx_writer(uint8_t *uid, uint32_t *blks) {
    uint32_t hi = 0, lo = 0;
    for (int i = 0; i < 4; i++) hi = (hi << 8) | uid[i];
    for (int i = 4; i < 8; i++) lo = (lo << 8) | uid[i];
    blks[0] = T5577_IDTECK_CONFIG;
    blks[1] = hi;
    blks[2] = lo;
    return IDTECK_T55XX_BLOCK_COUNT;
}
