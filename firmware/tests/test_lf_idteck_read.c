// Real PSK1 codec, RF/2 phase at RF/32, without Nordic hardware.
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

#include "protocols/idteck.h"

static const uint8_t frame[8] = {0x49, 0x44, 0x54, 0x4B, 0x35, 0x1F, 0xBE, 0x4B};
static const uint8_t captured_frame[8] = {0x49, 0x44, 0x54, 0x4B, 0xBF, 0xBB, 0xFD, 0x07};
static int16_t samples[32 * 64 * 5];

static void synthesize(bool inverted, int dc, bool changing) {
    for (unsigned i = 0; i < sizeof(samples) / sizeof(samples[0]); i++) {
        unsigned bit = (i / 32) % 64;
        unsigned value = (frame[bit / 8] >> (7 - bit % 8)) & 1;
        if (changing && i / 2048 % 2 && bit == 55) value ^= 1;
        int phase = ((i & 1) ^ value ^ inverted) ? 1 : -1;
        int envelope = dc ? (int)(700 * sin(i * 0.02)) : 0;
        samples[i] = dc + envelope + phase * 120 + (int)(i * 13 % 31) - 15;
    }
}

static bool read_expected(size_t start, size_t end, const uint8_t *expected) {
    void *codec = idteck.alloc();
    idteck.decoder.start(codec, 0);
    bool ok = false;
    for (size_t i = start; i < end && !ok; i++) {
        if (idteck.decoder.feed(codec, (uint16_t)samples[i])) {
            assert(memcmp(idteck.get_data(codec), expected, sizeof(frame)) == 0);
            ok = true;
        }
    }
    idteck.free(codec);
    return ok;
}

static bool read_from(size_t start, size_t end) {
    return read_expected(start, end, frame);
}

int main(int argc, char **argv) {
    for (unsigned inverted = 0; inverted < 2; inverted++) {
        for (unsigned dc = 0; dc < 2; dc++) {
            synthesize(inverted, dc ? 8000 : 0, false);
            for (unsigned start = 0; start < 2048; start += 7)
                assert(read_from(start, sizeof(samples) / sizeof(samples[0])));
            // A single valid frame must not be accepted without confirmation.
            assert(!read_from(0, 2048));
        }
    }
    synthesize(false, 8000, true);
    assert(!read_from(0, sizeof(samples) / sizeof(samples[0])));

    // An eight-sample LC transient at a payload transition makes a weak
    // phase repeat 4944544B7FBBFD07 with a valid header. Confirmation in one
    // phase is insufficient: strong phases must recover the exact frame.
    for (unsigned i = 0; i < sizeof(samples) / sizeof(samples[0]); i++) {
        unsigned bit = (i / 32) % 64;
        unsigned value = (captured_frame[bit / 8] >> (7 - bit % 8)) & 1;
        int phase = ((i & 1) ^ value) ? 120 : -120;
        int pulse = bit == 33 && i % 32 >= 5 && i % 32 < 13 ? 1000 : 0;
        samples[i] = 8000 + phase + pulse + (int)(i * 13 % 31) - 15;
    }
    for (unsigned start = 0; start < 32; start++)
        assert(read_expected(start, sizeof(samples) / sizeof(samples[0]), captured_frame));
    for (unsigned i = 0; i < sizeof(samples) / sizeof(samples[0]); i++)
        samples[i] = 8000 + (int)(i * 103 % 1001) - 500;
    assert(!read_from(0, sizeof(samples) / sizeof(samples[0])));

    if (argc > 1) {
        // USB capture of a real T5577 programmed as PSK1 RF/32. Skip startup,
        // then repeat a complete 2048-sample period. Joining all 4000 samples
        // would splice a truncated frame and create artificial bit errors.
        uint8_t capture[4000];
        FILE *input = fopen(argv[1], "rb");
        assert(input && fread(capture, 1, sizeof(capture), input) == sizeof(capture));
        fclose(input);
        for (unsigned offset = 0; offset < 32; offset++) {
            void *codec = idteck.alloc();
            idteck.decoder.start(codec, 0);
            bool ok = false;
            for (unsigned i = offset; i < 2048 * 4 && !ok; i++)
                ok = idteck.decoder.feed(codec, (uint16_t)capture[1024 + i % 2048] << 5);
            if (!ok || memcmp(idteck.get_data(codec), captured_frame, sizeof(captured_frame)) != 0)
                fprintf(stderr, "IDTECK capture failed at sample offset %u (decoded=%u)\n", offset, ok);
            assert(ok && memcmp(idteck.get_data(codec), captured_frame, sizeof(captured_frame)) == 0);
            idteck.free(codec);
        }
    }
    puts("PASS: IDTECK PSK1, all phases/polarities, signed samples, two-frame confirmation, changing IDs and noise rejected");
}
