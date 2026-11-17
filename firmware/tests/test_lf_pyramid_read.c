// Host regression test: the real Pyramid codec and FSK demodulator, fed with a
// synthetic FSK2a RF/50 signal as the reader's SAADC sees it. The read must not
// depend on where in a bit the reader starts sampling.
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "protocols/pyramid.h"

// A real Farpointe tag's frame (FC 82, CN 12725, 26-bit).
static const uint8_t frame[16] = {0x00, 0x01, 0x01, 0x4A, 0x3E, 0x01, 0x01, 0x01,
                                  0x01, 0x01, 0x01, 0x6B, 0xE3, 0x8C, 0xD6, 0x4A};

static unsigned rng_state = 1;
static double noise(void) {
    // Sum of uniforms: roughly Gaussian, unit variance.
    double s = 0;
    for (int i = 0; i < 12; i++) {
        rng_state = rng_state * 1103515245u + 12345u;
        s += (double)((rng_state >> 8) & 0xFFFF) / 65536.0;
    }
    return s - 6.0;
}

// Sub-carrier as the tag sends it: whole fc/8 (bit 0) or fc/10 (bit 1) cycles
// up to each bit's RF/50 boundary, one ADC sample per carrier cycle.
static size_t synthesize(uint16_t *out, size_t capacity, unsigned frames, double amplitude, double sigma) {
    size_t n = 0;
    unsigned emitted = 0;
    for (unsigned f = 0; f < frames; f++) {
        for (unsigned bi = 0; bi < 128; bi++) {
            unsigned bit = (frame[bi / 8] >> (7 - bi % 8)) & 1;
            unsigned top = bit ? 10 : 8;
            unsigned boundary = (f * 128 + bi + 1) * 50;
            while (emitted < boundary) {
                for (unsigned t = 0; t < top && n < capacity; t++) {
                    double level = t < top / 2 ? amplitude : -amplitude;
                    double v = 8000.0 + level + sigma * noise();
                    out[n++] = (uint16_t)(v < 0 ? 0 : v > 16383 ? 16383 : v);
                }
                emitted += top;
            }
        }
    }
    return n;
}

static uint16_t signal[50 * 128 * 4];

static bool read_from(size_t start, size_t count) {
    void *codec = pyramid.alloc();
    pyramid.decoder.start(codec, 0);
    bool ok = false;
    for (size_t i = start; i < count && !ok; i++) {
        if (pyramid.decoder.feed(codec, signal[i])) {
            ok = memcmp(pyramid.get_data(codec), frame, sizeof(frame)) == 0;
            assert(ok);  // parity and CRC must never let a wrong frame through
        }
    }
    pyramid.free(codec);
    return ok;
}

int main(void) {
    // Three frames: a reader starting anywhere in the first one has two more.
    size_t count = synthesize(signal, sizeof(signal) / sizeof(signal[0]), 3, 600, 0);
    unsigned failed = 0;
    for (size_t start = 0; start < 50 * 128; start += 7) {
        if (!read_from(start, count)) {
            failed++;
            if (failed <= 10) fprintf(stderr, "clean signal: no read starting at sample %zu (bit phase %zu)\n", start, start % 50);
        }
    }
    unsigned noisy_failed = 0, noisy_total = 0;
    for (unsigned trial = 0; trial < 200; trial++) {
        rng_state = trial + 1;
        count = synthesize(signal, sizeof(signal) / sizeof(signal[0]), 3, 300, 300);
        size_t start = (trial * 6421u) % (50 * 128);
        noisy_total++;
        if (!read_from(start, count)) noisy_failed++;
    }
    printf("Pyramid: clean %u of %u starts failed; noisy (SNR 0 dB) %u of %u failed\n",
           failed, (50 * 128 + 6) / 7, noisy_failed, noisy_total);
    assert(failed == 0);
    assert(noisy_failed * 20 <= noisy_total);  // at least 95%
    // No tag: noise only must not decode.
    for (size_t i = 0; i < count; i++) signal[i] = (uint16_t)(8000 + 300 * noise());
    assert(!read_from(0, count));
    puts("PASS: Pyramid read at every bit phase, clean and noisy; noise alone does not decode");
}
