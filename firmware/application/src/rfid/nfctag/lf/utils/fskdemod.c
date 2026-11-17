#include "fskdemod.h"

#include <stdlib.h>
#include <math.h>
#include <string.h>

#define PI 3.14159265358979f
#define GOERTZEL(FREQ, SAMPLE_RATE) (2.0 * cos((2.0 * PI * FREQ) / (SAMPLE_RATE)))

// Goertzel squared magnitude (|X|^2) with an optional DC offset subtracted from
// each sample.
//
// No sqrt: the bit decision only compares two of these and power is monotonic
// with magnitude, so the sqrt is dropped (the Cortex-M4F FPU has no double
// hardware; a double sqrt() would pull in a soft-float routine). The result can
// round slightly negative near zero signal, which is harmless for a comparison.
//
// DC offset: a 50-sample (RF/50) window holds an integer number of fc/10 cycles
// but a non-integer count of fc/8, so the raw ADC's DC component leaks into the
// fc/8 bin and biases the decision toward fc/8. Subtracting a running baseline
// (dc) removes that bias; pass dc=0 for the legacy behaviour.
static float goertzel_power(float coef, const uint16_t samples[], int n, float dc) {
    float z1 = 0;
    float z2 = 0;
    for (int i = 0; i < n; i++) {
        float z0 = coef * z1 - z2 + ((float)samples[i] - dc);
        z2 = z1;
        z1 = z0;
    }
    return z1 * z1 + z2 * z2 - coef * z1 * z2;
}

void fsk_free(fsk_t *m) {
    if (m != NULL) {
        free(m);
    }
}

/**
 * Changed from fixed BITRATE constant to dynamic parameter to allow
 * multiple protocols with different speeds.
 */
static bool fsk_feed_aligned(fsk_t *m, uint16_t sample, bool *bit);

bool fsk_feed(fsk_t *m, uint16_t sample, bool *bit) {
    if (m->align) return fsk_feed_aligned(m, sample, bit);
    if (m->dc_block) {
        // Slow IIR baseline tracker (time const ~64 samples). Removes the large
        // raw-ADC DC offset that otherwise leaks into the fc/8 Goertzel bin and
        // pins every decision to bit 0. Unlike a per-window mean, a running
        // baseline does not distort fc/8's non-integer cycle count in a window.
        if (!m->dc_seen) {
            m->dc_run = (float)sample;
            m->dc_seen = true;
        } else {
            m->dc_run += ((float)sample - m->dc_run) * (1.0f / 64.0f);
        }
    }
    m->samples[m->c++] = sample;
    if (m->c < m->bitrate) {
        return false;
    }
    float dc = m->dc_block ? m->dc_run : 0.0f;
    float bit0 = goertzel_power(m->goertzel_fc_8,  m->samples, m->bitrate, dc);
    float bit1 = goertzel_power(m->goertzel_fc_10, m->samples, m->bitrate, dc);
    *bit = (bit1 > bit0);

    // Reset counter and clear sample buffer for the next bit
    m->c = 0;
    memset(m->samples, 0, sizeof(uint16_t) * m->bitrate);
    return true;
}

static void fsk_track_dc(fsk_t *m, uint16_t sample) {
    if (!m->dc_block) return;
    if (!m->dc_seen) {
        m->dc_run = (float)sample;
        m->dc_seen = true;
    } else {
        m->dc_run += ((float)sample - m->dc_run) * (1.0f / 64.0f);
    }
}

// fc/8 and fc/10 both repeat every 40 samples: cos/sin of each, per sample.
static float m_tone[40][4];
static bool m_tone_ready;

static void fsk_tone_init(void) {
    if (m_tone_ready) return;
    for (int n = 0; n < 40; n++) {
        m_tone[n][0] = cosf(2.0f * PI * n / 8.0f);
        m_tone[n][1] = sinf(2.0f * PI * n / 8.0f);
        m_tone[n][2] = cosf(2.0f * PI * n / 10.0f);
        m_tone[n][3] = sinf(2.0f * PI * n / 10.0f);
    }
    m_tone_ready = true;
}

// Each sample is correlated once with both tones against a fixed time
// reference and summed per block of step samples. A window's tone power is
// then |sum of its blocks|^2 -- the same as a Goertzel over the window, since
// a shifted time reference only turns the phase -- at a fraction of the cost
// of a Goertzel per window, which at 125 kS/s did not keep up.
static bool fsk_feed_aligned(fsk_t *m, uint16_t sample, bool *bit) {
    fsk_track_dc(m, sample);
    float x = (float)sample - (m->dc_block ? m->dc_run : 0.0f);
    const float *t = m_tone[m->tick];
    if (++m->tick == 40) m->tick = 0;
    m->sum[0] += x * t[0];
    m->sum[1] += x * t[1];
    m->sum[2] += x * t[2];
    m->sum[3] += x * t[3];
    if (++m->c < m->step) return false;
    m->c = 0;

    memcpy(m->ring[m->block], m->sum, sizeof(m->sum));
    memset(m->sum, 0, sizeof(m->sum));
    m->out_phase = m->block;
    if (++m->block == m->phases) m->block = 0;
    if (m->blocks < m->phases && ++m->blocks < m->phases) return false;

    float w[4] = {0, 0, 0, 0};
    for (uint8_t i = 0; i < m->phases; i++) {
        w[0] += m->ring[i][0];
        w[1] += m->ring[i][1];
        w[2] += m->ring[i][2];
        w[3] += m->ring[i][3];
    }
    float p0 = w[0] * w[0] + w[1] * w[1];  // fc/8: bit 0
    float p1 = w[2] * w[2] + w[3] * w[3];  // fc/10: bit 1
    *bit = p1 > p0;
    return true;
}

/**
 * Changed from fixed BITRATE constant to dynamic parameter to allow
 * multiple protocols with different speeds.
 */
fsk_t *fsk_alloc(uint8_t bitrate) {
    if (bitrate == 0 || bitrate > FSK_MAX_BITRATE) {
        return NULL;
    }

    fsk_t *m = (fsk_t *)malloc(sizeof(fsk_t));
    if (!m) return NULL;

    m->bitrate = bitrate;
    m->c = 0;
    m->dc_block = false;
    m->dc_seen = false;
    m->dc_run = 0.0f;
    m->goertzel_fc_8 = GOERTZEL(15625.0f, 125000.0f);
    m->goertzel_fc_10 = GOERTZEL(12500.0f, 125000.0f);
    // Windows per bit for align: 5 for RF/50 (every 10 samples), 4 for RF/64
    // (every 16); the best window is then at most an eighth of a bit off.
    m->phases = (bitrate % 5 == 0) ? 5 : 4;
    m->step = (uint8_t)(bitrate / m->phases);
    m->align = false;
    m->out_phase = 0;
    m->tick = 0;
    m->block = 0;
    m->blocks = 0;
    memset(m->sum, 0, sizeof(m->sum));
    memset(m->ring, 0, sizeof(m->ring));
    fsk_tone_init();
    return m;
}
