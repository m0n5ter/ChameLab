#include "hardnested_api.h"
#include <errno.h>
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>

int main(int argc, char **argv) {
    setvbuf(stdout, NULL, _IONBF, 0);
    setvbuf(stderr, NULL, _IONBF, 0);
    if (argc != 3) return 3;
    char *end;
    errno = 0;
    unsigned long long uid = strtoull(argv[1], &end, 16);
    if (errno != 0 || *end != '\0' || end == argv[1] || uid > UINT32_MAX) return 3;
    FILE *input = fopen(argv[2], "r");
    if (input == NULL) return 3;
    uint8_t seen[256] = {0};
    unsigned int distinct = 0;
    char line[64];
    while (fgets(line, sizeof(line), input) != NULL) {
        uint32_t nonce;
        unsigned int parity;
        char extra;
        if (sscanf(line, "%" SCNu32 "|%u %c", &nonce, &parity, &extra) != 2 || parity > 15) {
            fclose(input);
            return 3;
        }
        if (!seen[nonce >> 24]) {
            seen[nonce >> 24] = 1;
            distinct++;
        }
    }
    int failed = ferror(input);
    fclose(input);
    if (failed || distinct != 256) return 3;
    uint64_t key;
    int result = chamelab_hardnested_recover((uint32_t)uid, argv[2], &key);
    if (result == 1) {
        printf("CHAMELAB_KEY=%012" PRIX64 "\n", key);
        return 0;
    }
    return result == 0 ? 2 : 4;
}
