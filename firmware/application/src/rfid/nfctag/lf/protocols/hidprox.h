#pragma once

#include "protocols.h"
#include "utils/fskdemod.h"
#include "wiegand.h"

#define HIDPROX_DATA_SIZE (16)

typedef enum {
    STATE_SOF,
    STATE_DATA_LO,
    STATE_DATA_HI,
    STATE_DONE,
} hidprox_codec_state_t;

// Frame decoding for one window phase of the FSK demodulator (see fskdemod.h).
typedef struct {
    bool bit;
    uint8_t sof;
    uint64_t raw;
    uint8_t raw_length;
    hidprox_codec_state_t state;

    uint64_t last_raw;  // previous frame's bits, awaiting a match
    bool have_last;
} hidprox_lane_t;

#define HIDPROX_PHASES (5)  // fsk_alloc's windows per bit at RF/50

typedef struct {
    uint8_t data[HIDPROX_DATA_SIZE];

    fsk_t *modem;
    hidprox_lane_t lanes[HIDPROX_PHASES];

    uint8_t format_hint;
    wiegand_card_t *card;
} hidprox_codec;

extern const protocol hidprox;

uint8_t hidprox_t55xx_writer(wiegand_card_t *card, uint32_t *blks);
