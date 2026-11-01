/* Minimal Windows adapter for the two pthread operations used by nested_util.c.
 * Serial mode executes the same worker bodies synchronously. */
#pragma once
#include <windows.h>
#include <stdlib.h>
#include <process.h>
extern int bench_serial;
typedef HANDLE pthread_t;
typedef struct { void *(*fn)(void *); void *arg; } bench_task;
static unsigned __stdcall bench_worker(void *arg) {
    bench_task task = *(bench_task *)arg;
    free(arg);
    task.fn(task.arg);
    return 0;
}
static int pthread_create(pthread_t *thread, void *attr, void *(*fn)(void *), void *arg) {
    (void)attr;
    if (bench_serial) { fn(arg); *thread = NULL; return 0; }
    bench_task *task = malloc(sizeof(*task));
    if (!task) abort();
    task->fn = fn; task->arg = arg;
    *thread = (HANDLE)_beginthreadex(NULL, 0, bench_worker, task, 0, NULL);
    if (!*thread) abort();
    return 0;
}
static int pthread_join(pthread_t thread, void **result) {
    (void)result;
    if (thread) { WaitForSingleObject(thread, INFINITE); CloseHandle(thread); }
    return 0;
}
