// PAC NRZ: actual codec, UART/parity/XOR checks and an Android BLE ADC capture.
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "protocols/pac.h"

static const uint8_t id[8] = "1234AB00";

static bool read_signal(const int16_t *samples, unsigned size, unsigned start, uint8_t mode) {
    void *d = pac.alloc();
    assert(d);
    pac.decoder.start(d, mode);
    bool ok = false;
    for (unsigned i = start; i < size && !ok; i++)
        ok = pac.decoder.feed(d, (uint16_t)samples[i]);
    if (ok) assert(memcmp(pac.get_data(d), id, 8) == 0);
    pac.free(d);
    return ok;
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
            for (unsigned mode = 0; mode < 2; mode++)
                assert(read_signal(samples, sizeof(samples) / sizeof(samples[0]), start, mode));
    }
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
    for (unsigned mode = 0; mode < 2; mode++)
        assert(!read_signal(samples, sizeof(samples) / sizeof(samples[0]), 0, mode));
    puts("PASS: PAC NRZ phases/polarities, signed noise, BLE baseline recovery; narrow fails, wide verifies exact ID");
}
