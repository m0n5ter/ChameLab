#include <stdlib.h>
#include <string.h>
#include "nested_util.h"
int bench_serial;

/* Same nonce sweep as nested.c; exports the result without CLI formatting.
 * Algorithm sources are compiled unchanged. Calls must be sequential. */
__declspec(dllexport) int bench_nested(const uint32_t *v, int serial, uint64_t *output) {
    NtpKs1 *pnk = NULL;
    uint32_t count = 0, key_count = 0;
    bench_serial = serial;
    for (int i = 2; i < 8; i += 3) {
        uint8_t par[3];
        for (int b = 0; b < 3; b++) par[b] = (v[i + 2] >> b) & 1;
        uint32_t nt = prng_successor(v[i], v[1] - 14);
        for (uint32_t m = v[1] - 14; m <= v[1] + 14; m++) {
            uint32_t ks = v[i + 1] ^ nt;
            if (valid_nonce(nt, v[i + 1], ks, par)) {
                NtpKs1 *next = realloc(pnk, sizeof(*pnk) * (count + 1));
                if (!next) abort();
                pnk = next;
                pnk[count++] = (NtpKs1){nt, ks};
            }
            nt = prng_successor(nt, 1);
        }
    }
    if (!count) { free(pnk); return 0; }
    uint64_t *keys = nested(pnk, count, v[0], &key_count);
    if (key_count > 50 || (key_count && !keys)) abort();
    if (key_count) memcpy(output, keys, key_count * sizeof(*keys));
    free(keys);
    free(pnk);
    return (int)key_count;
}
