// Host regression test: the real HID Prox and ioProx codecs and FSK demodulator,
// fed with a synthetic FSK2a signal as the reader's SAADC sees it. A read must
// not depend on where in a bit the reader starts sampling.
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "protocols/hidprox.h"
#include "protocols/ioprox.h"

static unsigned rng_state = 1;
static double noise(void) {
    double s = 0;  // sum of 12 uniforms: roughly Gaussian, unit variance
    for (int i = 0; i < 12; i++) {
        rng_state = rng_state * 1103515245u + 12345u;
        s += (double)((rng_state >> 8) & 0xFFFF) / 65536.0;
    }
    return s - 6.0;
}

static uint16_t signal[64 * 96 * 6];
static size_t signal_len;

// Sub-carrier as a tag sends it: whole fc/8 (bit 0) or fc/10 (bit 1) cycles up
// to each bit's boundary, one ADC sample per carrier cycle.
static void synthesize(const uint8_t *bits, unsigned count, unsigned bitrate, unsigned frames, double amplitude, double sigma) {
    signal_len = 0;
    unsigned emitted = 0;
    for (unsigned f = 0; f < frames; f++) {
        for (unsigned bi = 0; bi < count; bi++) {
            unsigned top = bits[bi] ? 10 : 8;
            unsigned boundary = (f * count + bi + 1) * bitrate;
            while (emitted < boundary) {
                for (unsigned t = 0; t < top && signal_len < sizeof(signal) / sizeof(signal[0]); t++) {
                    double v = 8000.0 + (t < top / 2 ? amplitude : -amplitude) + sigma * noise();
                    signal[signal_len++] = (uint16_t)(v < 0 ? 0 : v > 16383 ? 16383 : v);
                }
                emitted += top;
            }
        }
    }
}

static bool read_from(const protocol *p, size_t start, const uint8_t *expected, size_t size) {
    void *codec = p->alloc();
    p->decoder.start(codec, 0);
    bool ok = false;
    for (size_t i = start; i < signal_len && !ok; i++) {
        if (p->decoder.feed(codec, signal[i])) {
            ok = memcmp(p->get_data(codec), expected, size) == 0;
            if (!ok) {
                fprintf(stderr, "%u decoded a wrong ID starting at %zu\n", p->tag_type, start);
                abort();
            }
        }
    }
    p->free(codec);
    return ok;
}

// Every 7th start over a frame, clean; then 200 noisy reads at SNR 0 dB. Six
// frames: the bit phase settles, then two frames must agree.
static void check(const char *name, const protocol *p, const uint8_t *bits, unsigned count, unsigned bitrate,
                  const uint8_t *expected, size_t size) {
    synthesize(bits, count, bitrate, 6, 600, 0);
    unsigned failed = 0, starts = 0;
    for (size_t start = 0; start < (size_t)count * bitrate; start += 7, starts++) {
        if (!read_from(p, start, expected, size) && ++failed <= 5)
            printf("%s: no clean read starting at sample %zu (bit phase %zu)\n", name, start, start % bitrate);
    }
    unsigned noisy_failed = 0;
    for (unsigned trial = 0; trial < 200; trial++) {
        rng_state = trial + 1;
        synthesize(bits, count, bitrate, 6, 300, 300);
        if (!read_from(p, (trial * 6421u) % (count * bitrate), expected, size)) noisy_failed++;
    }
    printf("%s: clean %u of %u starts failed; noisy (SNR 0 dB) %u of 200 failed\n", name, failed, starts, noisy_failed);
    assert(failed == 0);
    assert(noisy_failed <= 10);  // at least 95%
    for (size_t i = 0; i < signal_len; i++) signal[i] = (uint16_t)(8000 + 300 * noise());
    assert(!read_from(p, 0, expected, size));  // noise alone must not decode
}

int main(void) {
    setvbuf(stdout, NULL, _IONBF, 0);
    // HID Prox H10301 26-bit, FC 123, CN 45678: FSK2a RF/50, 96-bit frame.
    wiegand_card_t card = {.format = H10301, .facility_code = 123, .card_number = 45678};
    uint32_t blocks[4];
    assert(hidprox_t55xx_writer(&card, blocks) == 4);
    uint8_t hid_bits[96];
    for (unsigned i = 0; i < 96; i++) hid_bits[i] = (blocks[1 + i / 32] >> (31 - i % 32)) & 1;
    uint8_t hid_expected[11] = {H10301, 0, 0, 0, 123, 0, 0, 0, 0xB2, 0x6E, 0};
    check("HID Prox", &hidprox, hid_bits, 96, 50, hid_expected, sizeof(hid_expected));

    // ioProx version 1, FC 2, CN 3456: FSK2a RF/64, 64-bit frame.
    uint8_t io_expected[16];
    assert(ioprox_encode_params_to_data(1, 2, 3456, io_expected));
    uint8_t io_bits[64];
    for (unsigned i = 0; i < 64; i++) io_bits[i] = (io_expected[4 + i / 8] >> (7 - i % 8)) & 1;
    check("ioProx", &ioprox, io_bits, 64, 64, io_expected, 12);

    puts("PASS: HID Prox and ioProx read at every bit phase, clean and noisy; noise alone does not decode");
}
