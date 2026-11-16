#pragma once

#include <stdbool.h>
#include <stdint.h>

#include "tag_base_type.h"

// The slot's own card occupies the first 16 bytes. Both v4 (timed cycling) and
// v5 store the same typed extra cards at byte 24. In v4, bytes 19/20 held the
// bursts and silence of the only (while-present) mode. v5 holds the mode, bursts
// and silence in bytes 19-21; v5 records written before the mode existed have
// zeros there, which reads as the per-field mode.
#define LF_MAX_TAG_ID_SIZE 16
#define LF_CYCLE_MAX 8
#define LF_CYCLE_MARKER 16
#define LF_CYCLE_VERSION 17
#define LF_CYCLE_COUNT 18
#define LF_CYCLE_MODE 19
#define LF_CYCLE_BURSTS 20
#define LF_CYCLE_SILENCE 21
#define LF_CYCLE_EXTRAS 24
#define LF_CYCLE_ENTRY_SIZE (2 + LF_MAX_TAG_ID_SIZE)
#define LF_CYCLE_SIZE 152

// Next card on each field appearance (one steady card per tap).
#define LF_CYCLE_MODE_PER_FIELD 0
// Cards in turn while the field is present, `bursts` per card and `silence` between cards.
#define LF_CYCLE_MODE_WHILE_PRESENT 1
#define LF_CYCLE_BURSTS_MAX 10
// In 100 ms units.
#define LF_CYCLE_SILENCE_MAX 30

typedef struct {
    uint8_t mode;
    uint8_t bursts;
    uint8_t silence;
} lf_cycle_settings_t;

// Emulator data size of an LF tag type; 0 if the type is not emulated.
uint16_t lf_tag_id_size(tag_specific_type_t type);
bool lf_cycle_valid(const uint8_t *data, uint16_t length);
// Validates the complete list before upgrading v4 in place for the next flash save.
bool lf_cycle_prepare_save(uint8_t *data, uint16_t length);
// The settings of a valid record (out-of-range values fall back to the defaults);
// the defaults (per field, one burst, no silence) for anything else.
lf_cycle_settings_t lf_cycle_settings(const uint8_t *data, uint16_t length);
bool lf_cycle_settings_valid(lf_cycle_settings_t settings);
// Stores settings into a v5 record.
void lf_cycle_store_settings(uint8_t *data, lf_cycle_settings_t settings);
