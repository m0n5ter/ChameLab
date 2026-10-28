#include <stdio.h>
#include <inttypes.h>
#include "crapto1.h"
#include "parity.h"

int main(int argc, char **argv) {
    if (argc != 2) return 1;
    FILE *output = fopen(argv[1], "w");
    if (output == NULL) return 1;
    uint32_t random = 0x9325abcd;
    for (unsigned int sample = 0; sample < 12000; sample++) {
        random ^= random << 13;
        random ^= random >> 17;
        random ^= random << 5;
        struct Crypto1State state;
        crypto1_init(&state, 0x112233445566ULL);
        uint32_t encrypted = 0;
        uint8_t parity = 0;
        for (int shift = 24; shift >= 0; shift -= 8) {
            uint8_t plain = (uint8_t)(random >> shift);
            uint8_t stream = crypto1_byte(&state, plain ^ (uint8_t)(0x12345678u >> shift), 0);
            encrypted = (encrypted << 8) | (plain ^ stream);
            parity = (parity << 1) | (oddparity8(plain) ^ filter(state.odd));
        }
        fprintf(output, "%" PRIu32 "|%u\n", encrypted, parity);
    }
    return fclose(output) != 0;
}
