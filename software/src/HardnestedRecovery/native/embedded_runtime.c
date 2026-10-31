#include "embedded_runtime.h"
#include "cmdhfmfhard.h"
#include <setjmp.h>

typedef struct allocation {
    struct allocation *previous, *next;
    void *base;
    size_t size;
} allocation;
static allocation *allocations;
static size_t allocated, limit;
static FILE *files[8];
static jmp_buf recovery;
static hn_callback receive;
static void *receive_context;

_Noreturn void hn_fail(int status) { longjmp(recovery, status); }
void hn_poll(void) {
    if (receive && receive(0, NULL, receive_context)) hn_fail(-2);
}
void hn_progress(const char *message) {
    if (receive && receive(1, message, receive_context)) hn_fail(-2);
}

void *hn_memalign(size_t alignment, size_t size) {
    hn_poll();
    if (alignment < _Alignof(allocation)) alignment = _Alignof(allocation);
    if ((alignment & (alignment - 1)) || size > limit - allocated ||
        size > SIZE_MAX - sizeof(allocation) - alignment) hn_fail(-3);
    void *base = malloc(size + sizeof(allocation) + alignment);
    if (!base) hn_fail(-3);
    uintptr_t address = ((uintptr_t)base + sizeof(allocation) + alignment - 1) & ~(uintptr_t)(alignment - 1);
    allocation *entry = (allocation *)address - 1;
    *entry = (allocation){NULL, allocations, base, size};
    if (allocations) allocations->previous = entry;
    allocations = entry;
    allocated += size;
    return (void *)address;
}
void *hn_malloc(size_t size) { return hn_memalign(16, size); }
void *hn_calloc(size_t count, size_t size) {
    if (size && count > SIZE_MAX / size) hn_fail(-3);
    void *result = hn_malloc(count * size);
    memset(result, 0, count * size);
    return result;
}
void hn_free(void *ptr) {
    if (!ptr) return;
    allocation *entry = (allocation *)ptr - 1;
    if (entry->previous) entry->previous->next = entry->next;
    else allocations = entry->next;
    if (entry->next) entry->next->previous = entry->previous;
    allocated -= entry->size;
    free(entry->base);
}
void *hn_realloc(void *ptr, size_t size) {
    if (!ptr) return hn_malloc(size);
    if (!size) { hn_free(ptr); return NULL; }
    size_t old = ((allocation *)ptr - 1)->size;
    void *result = hn_malloc(size);
    memcpy(result, ptr, old < size ? old : size);
    hn_free(ptr);
    return result;
}
int hn_posix_memalign(void **ptr, size_t alignment, size_t size) {
    *ptr = hn_memalign(alignment, size);
    return 0;
}
static FILE *track_file(FILE *file) {
    if (!file) hn_fail(-4);
    for (size_t i = 0; i < 8; i++) if (!files[i]) { files[i] = file; return file; }
    fclose(file);
    hn_fail(-4);
    return NULL;
}
FILE *hn_fopen(const char *path, const char *mode) { return track_file(fopen(path, mode)); }
FILE *hn_fmemopen(void *data, size_t size, const char *mode) {
    return track_file(fmemopen(data, size, mode));
}
int hn_fclose(FILE *file) {
    for (size_t i = 0; i < 8; i++) if (files[i] == file) files[i] = NULL;
    return fclose(file);
}

/* Return values: 1 key, 0 exhausted, -1 invalid capture, -2 cancelled,
 * -3 memory budget/allocation failure, -4 native input/decoder failure.
 * Callers must serialize access: the upstream solver uses global state. */
int chamelab_hardnested_embedded(uint32_t uid, const char *path, uint64_t *key,
    size_t memory_limit, hn_callback callback, void *context) {
    if (!path || !key || !memory_limit) return -1;
    *key = 0;
    limit = memory_limit;
    receive = callback;
    receive_context = context;
    int status = setjmp(recovery);
    if (!status) {
        hn_poll();
        // Check input before allocating the solver's large state tables.
        FILE *input = hn_fopen(path, "r");
        hn_fclose(input);
        status = mfnestedhard(0, 0, NULL, 0, 0, NULL, false, false, false, key, NULL, uid, (char *)path);
    }
    for (size_t i = 0; i < 8; i++) if (files[i]) { fclose(files[i]); files[i] = NULL; }
    while (allocations) hn_free(allocations + 1);
    receive = NULL;
    receive_context = NULL;
    return status;
}
