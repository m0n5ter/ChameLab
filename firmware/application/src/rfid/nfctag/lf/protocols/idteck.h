#pragma once

#include "protocols.h"

// IDTECK frame layout: 32-bit fixed preamble + 32-bit card payload.
#define IDTECK_DATA_SIZE   (8)             // 8 bytes = 64 bits on air
#define IDTECK_BIT_COUNT   (64)
#define IDTECK_PREAMBLE    (0x4944544BU)   // ASCII "IDTK", MSB first on air
#define IDTECK_RF_PER_BIT  (32)
#define IDTECK_PHASES      (32)

typedef struct {
    uint64_t bits;
    uint64_t previous;
    uint8_t count;
    bool have_previous;
    bool confirmed;
    uint32_t bits_seen;
    uint32_t energy;
    uint32_t previous_energy;
    uint32_t previous_at;
    uint32_t confidence;
} idteck_lane;

typedef struct {
    uint8_t data[IDTECK_DATA_SIZE];
    int16_t samples[IDTECK_RF_PER_BIT];
    int32_t correlation;
    uint8_t position;
    uint8_t filled;
    uint8_t step;
    uint8_t lane;
    uint8_t settle;
    idteck_lane lanes[IDTECK_PHASES];
} idteck_codec;

extern const protocol idteck;

uint8_t idteck_t55xx_writer(uint8_t *uid, uint32_t *blks);
