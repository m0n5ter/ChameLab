#pragma once

#include "protocols.h"

// Internal acquisition alternative for AC-coupled NRZ with a recovering baseline.
#define PAC_DECODE_WIDE_HYSTERESIS 90
#define PAC_DECODE_UNCLIPPED 0x80

extern const protocol pac;
uint8_t pac_t55xx_writer(uint8_t *data, uint32_t *blks);
