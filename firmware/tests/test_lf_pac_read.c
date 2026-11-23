// PAC NRZ: actual codec, UART/parity/XOR checks and an Android BLE ADC capture.
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <string.h>
#include "protocols/pac.h"

static const uint8_t id[8] = "1234AB00";

static bool read_expected(const int16_t *samples, unsigned size, unsigned start, uint8_t mode, const uint8_t *expected) {
    void *d = pac.alloc();
    assert(d);
    pac.decoder.start(d, mode);
    bool ok = false;
    for (unsigned i = start; i < size && !ok; i++)
        ok = pac.decoder.feed(d, (uint16_t)samples[i]);
    if (ok) assert(memcmp(pac.get_data(d), expected, 8) == 0);
    pac.free(d);
    return ok;
}

static bool read_signal(const int16_t *samples, unsigned size, unsigned start, uint8_t mode) {
    return read_expected(samples, size, start, mode, id);
}

int main(int argc, char **argv) {
    int16_t samples[4096 * 5];
    uint32_t blocks[5];
    assert(pac_t55xx_writer((uint8_t *)id, blocks) == 5);
    for (unsigned inv = 0; inv < 2; inv++) {
        for (unsigned i = 0; i < sizeof(samples) / sizeof(samples[0]); i++) {
            unsigned bit = i / 32 % 128;
            bool value = ((blocks[1 + bit / 32] >> (31 - bit % 32)) & 1) ^ inv;
            samples[i] = (value ? 8000 : 0) + (int)(i * 13 % 31) - 15;
        }
        for (unsigned start = 0; start < 4096; start += 31)
            for (unsigned unclipped = 0; unclipped < 2; unclipped++)
                for (unsigned width = 75; width <= 95; width += 5)
                    assert(read_signal(samples, sizeof(samples) / sizeof(samples[0]), start,
                        width | (unclipped ? PAC_DECODE_UNCLIPPED : 0)));
    }
    // AC coupling returns both states toward the same baseline. A mid-run
    // 9k recovery pulse and a true 15k rising excursion both clip to 8k;
    // widening a clipped threshold alone cannot distinguish those events.
    unsigned edge = 0;
    bool previous = true;
    for (unsigned i = 0; i < sizeof(samples) / sizeof(samples[0]); i++) {
        unsigned bit = i / 32 % 128;
        bool value = (blocks[1 + bit / 32] >> (31 - bit % 32)) & 1;
        if (value != previous) { edge = i; previous = value; }
        double sample = 5000 + (value ? 10000 : -14000) * exp(-(double)(i - edge) / 40);
        if (sample < 0) sample = 0;
        if (bit == 14) sample += 5000;
        samples[i] = (int16_t)sample;
    }
    samples[0] = 1; // Prescan sees a low excursion and chooses its 8k clip floor.
    assert(!read_signal(samples, sizeof(samples) / sizeof(samples[0]), 0, 75));
    assert(!read_signal(samples, sizeof(samples) / sizeof(samples[0]), 0, PAC_DECODE_WIDE_HYSTERESIS));
    for (unsigned start = 0; start < 32; start++)
        assert(read_signal(samples, sizeof(samples) / sizeof(samples[0]), start,
            PAC_DECODE_UNCLIPPED | PAC_DECODE_WIDE_HYSTERESIS));
    if (argc > 1) {
        unsigned char capture[4000];
        FILE *f = fopen(argv[1], "rb");
        assert(f && fread(capture, 1, sizeof(capture), f) == sizeof(capture));
        fclose(f);
        // LF sniff exports 4000 samples, three bits short of a PAC period.
        // Preserve every recorded sample; append the known final two low bits
        // and stop bit. Repeat this explicitly hybrid period, not a truncated
        // capture joined to itself. All troublesome transitions are recorded.
        for (unsigned i = 0; i < sizeof(samples) / sizeof(samples[0]); i++) {
            unsigned pos = i % 4096;
            samples[i] = pos < 4000 ? capture[pos] * 32 : pos < 4064 ? 0 : 10000;
        }
        assert(!read_signal(samples, sizeof(samples) / sizeof(samples[0]), 0, 0));
        for (unsigned start = 0; start < 32; start++)
            assert(read_signal(samples, sizeof(samples) / sizeof(samples[0]), start, PAC_DECODE_WIDE_HYSTERESIS));
    }
    memset(samples, 0, sizeof(samples));
    for (unsigned unclipped = 0; unclipped < 2; unclipped++)
        for (unsigned width = 75; width <= 95; width += 5)
            assert(!read_signal(samples, sizeof(samples) / sizeof(samples[0]), 0,
                width | (unclipped ? PAC_DECODE_UNCLIPPED : 0)));
    for (int fixture = 2; fixture < argc; fixture++) {
        FILE *f = fopen(argv[fixture], "rb");
        assert(f && fread(samples, sizeof(*samples), 12288, f) == 12288);
        fclose(f);
        const uint8_t *expected = (const uint8_t *)(fixture == 4 ? "1234AB01" : "1234AB00");
        const uint8_t modes[] = {75, 90, 95, PAC_DECODE_UNCLIPPED | 75, PAC_DECODE_UNCLIPPED | 90};
        for (unsigned start = 0; start < 32; start++) {
            bool found = false;
            for (unsigned mode = 0; mode < sizeof(modes); mode++)
                found |= read_expected(samples, 12288, start, modes[mode], expected);
            if (!found) fprintf(stderr, "PAC failed hardware fixture %d offset %u\n", fixture, start);
            assert(found);
        }
    }
    puts("PASS: PAC NRZ phases/polarities, signed noise, BLE baseline recovery; narrow fails, wide verifies exact ID");
}
