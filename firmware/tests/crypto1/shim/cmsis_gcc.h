/* Host-build shim for cmsis_gcc.h — only what mf1_crapto1.c / mf1_crypto1.c use. */
#ifndef HOST_SHIM_CMSIS_GCC_H
#define HOST_SHIM_CMSIS_GCC_H

#include <stdint.h>

static inline uint32_t __REV(uint32_t v) {
    return __builtin_bswap32(v);
}

/* Data memory barrier. On Cortex-M this compiles to a DMB instruction; on
 * the host we only need a compiler barrier since the harness is single-thread. */
#ifndef __DMB
static inline void __DMB(void) {
    __asm__ volatile("" ::: "memory");
}
#endif

#endif
