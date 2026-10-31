#define HN_RUNTIME_IMPLEMENTATION
#include "embedded_runtime.h"

static int polls;
static int cancel_after;
static int receive(int event, const char *message, void *context) {
    (void)context;
    if (cancel_after == 0)
        return event == 1 && message && strstr(message, "precalculated bitflip state tables");
    return ++polls >= cancel_after;
}
static int check(int actual, int expected, const char *label) {
    if (actual == expected) return 0;
    fprintf(stderr, "%s: got %d, expected %d\n", label, actual, expected);
    return 1;
}

int main(int argc, char **argv) {
    if (argc != 2) return 1;
    uint64_t key;
    const size_t limit = 1792U * 1024U * 1024U;
    cancel_after = 1; polls = 0;
    if (check(chamelab_hardnested_embedded(0x12345678, argv[1], &key, limit, receive, NULL), -2, "early cancellation")) return 1;
    cancel_after = 1000; polls = 0;
    if (check(chamelab_hardnested_embedded(0x12345678, argv[1], &key, limit, receive, NULL), -2, "cancellation during computation")) return 1;
    cancel_after = 0;
    if (check(chamelab_hardnested_embedded(0x12345678, argv[1], &key, limit, receive, NULL), -2, "cancellation after loading state tables")) return 1;
    if (check(chamelab_hardnested_embedded(0x12345678, argv[1], &key, 512U * 1024U * 1024U, NULL, NULL), -3, "memory budget after cancellation")) return 1;
    if (check(chamelab_hardnested_embedded(0x12345678, argv[1], &key, 1024, NULL, NULL), -3, "retry after memory failure")) return 1;
    if (check(chamelab_hardnested_embedded(0x12345678, "/chamelab-missing-capture.txt", &key, limit, NULL, NULL), -4, "input error must not exit the host")) return 1;
    for (int i = 0; i < 2; i++) {
        if (check(chamelab_hardnested_embedded(0x12345678, argv[1], &key, limit, NULL, NULL), 1, "recovery after cancellation")) return 1;
        if (key != 0x112233445566ULL) { fprintf(stderr, "Wrong recovered key\n"); return 1; }
    }
    puts("Embedded engine: cancellation, memory budget and repeated known-key recovery passed.");
    return 0;
}
