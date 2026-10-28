#ifndef CHAMELAB_COMPILER_H
#define CHAMELAB_COMPILER_H

#include <stdint.h>
#include <intrin.h>
#include <malloc.h>

#define __BIGGEST_ALIGNMENT__ 16
#define restrict __restrict
#define __builtin_assume_aligned(pointer, alignment) (pointer)

static __inline unsigned int chamelab_clz(uint32_t value) {
    unsigned long index;
    return _BitScanReverse(&index, value) ? 31 - index : 32;
}

static __inline unsigned int chamelab_popcount(uint32_t value) {
    value -= (value >> 1) & 0x55555555;
    value = (value & 0x33333333) + ((value >> 2) & 0x33333333);
    return (((value + (value >> 4)) & 0x0f0f0f0f) * 0x01010101) >> 24;
}

#define __builtin_clz(value) chamelab_clz(value)
#define __builtin_popcount(value) chamelab_popcount(value)
#define __builtin_bswap16(value) _byteswap_ushort(value)

#endif
