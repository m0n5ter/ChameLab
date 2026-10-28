#ifndef CHAMELAB_PTHREAD_H
#define CHAMELAB_PTHREAD_H

#include <windows.h>
#include <process.h>
#include <stdlib.h>

typedef HANDLE pthread_t;
typedef SRWLOCK pthread_mutex_t;
#define PTHREAD_MUTEX_INITIALIZER SRWLOCK_INIT

typedef struct {
    void *(*function)(void *);
    void *argument;
} chamelab_thread_args;

static unsigned __stdcall chamelab_thread_start(void *argument) {
    chamelab_thread_args *args = argument;
    args->function(args->argument);
    free(args);
    return 0;
}

static __inline int pthread_create(pthread_t *thread, const void *attributes,
                                  void *(*function)(void *), void *argument) {
    chamelab_thread_args *args = malloc(sizeof(*args));
    if (args == NULL) abort();
    args->function = function;
    args->argument = argument;
    *thread = (HANDLE)_beginthreadex(NULL, 0, chamelab_thread_start, args, 0, NULL);
    if (*thread == NULL) abort();
    return 0;
}

static __inline int pthread_join(pthread_t thread, void **result) {
    WaitForSingleObject(thread, INFINITE);
    CloseHandle(thread);
    return 0;
}

static __inline int pthread_mutex_init(pthread_mutex_t *mutex, const void *attributes) {
    InitializeSRWLock(mutex);
    return 0;
}

static __inline int pthread_mutex_lock(pthread_mutex_t *mutex) {
    AcquireSRWLockExclusive(mutex);
    return 0;
}

static __inline int pthread_mutex_unlock(pthread_mutex_t *mutex) {
    ReleaseSRWLockExclusive(mutex);
    return 0;
}

#endif
