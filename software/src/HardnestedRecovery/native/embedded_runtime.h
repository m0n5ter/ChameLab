#ifndef CHAMELAB_EMBEDDED_RUNTIME_H
#define CHAMELAB_EMBEDDED_RUNTIME_H

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifndef __APPLE__
#include <malloc.h>
#endif
#include <pthread.h>
#include "hardnested_api.h"

/* Embedded builds run one solver thread. This makes cancellation/error unwinding
 * safe and works in an ordinary Web Worker without SharedArrayBuffer or COOP/COEP.
 * The desktop worker retains its existing parallel implementation. */
typedef int (*hn_callback)(int event, const char *message, void *context);
HARDNESTED_API int chamelab_hardnested_embedded(uint32_t uid, const char *path,
    uint64_t *key, size_t memory_limit, hn_callback callback, void *context);
void hn_poll(void);
void hn_progress(const char *message);
_Noreturn void hn_fail(int status);
void *hn_malloc(size_t size);
void *hn_calloc(size_t count, size_t size);
void *hn_realloc(void *ptr, size_t size);
void hn_free(void *ptr);
void *hn_memalign(size_t alignment, size_t size);
int hn_posix_memalign(void **ptr, size_t alignment, size_t size);
FILE *hn_fopen(const char *path, const char *mode);
FILE *hn_fmemopen(void *data, size_t size, const char *mode);
int hn_fclose(FILE *file);

#ifndef HN_RUNTIME_IMPLEMENTATION
#define malloc hn_malloc
#define calloc hn_calloc
#define realloc hn_realloc
#define free hn_free
#define memalign hn_memalign
#define posix_memalign hn_posix_memalign
#define _aligned_malloc(size, alignment) hn_memalign(alignment, size)
#define _aligned_free hn_free
#define fopen hn_fopen
#define fmemopen hn_fmemopen
#define fclose hn_fclose
#define exit(status) hn_fail(-4)
#define pthread_create(thread, attr, function, arg) (*(thread) = (pthread_t)0, (void)(attr), (function)(arg), 0)
#define pthread_join(thread, result) ((void)(thread), (void)(result), 0)
#define pthread_mutex_init(mutex, attr) ((void)(mutex), (void)(attr), 0)
#define pthread_mutex_lock(mutex) ((void)(mutex), 0)
#define pthread_mutex_unlock(mutex) ((void)(mutex), 0)
#endif
#endif
