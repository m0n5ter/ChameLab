#ifndef CHAMELAB_HARDNESTED_API_H
#define CHAMELAB_HARDNESTED_API_H

#include <stdint.h>

#ifdef _WIN32
#define HARDNESTED_API __declspec(dllexport)
#else
#define HARDNESTED_API __attribute__((visibility("default")))
#endif

HARDNESTED_API int chamelab_hardnested_recover(uint32_t uid, const char *path, uint64_t *key);

#endif
