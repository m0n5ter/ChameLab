#pragma once

#include <stdbool.h>
#include <stdint.h>

#include "tag_base_type.h"

// The slot's own card occupies the first 16 bytes. Both v4 (timed cycling) and
// v5 (one card per field appearance) store the same typed extra cards at byte 24.
#define LF_MAX_TAG_ID_SIZE 16
#define LF_CYCLE_MAX 8
#define LF_CYCLE_MARKER 16
#define LF_CYCLE_VERSION 17
#define LF_CYCLE_COUNT 18
#define LF_CYCLE_EXTRAS 24
#define LF_CYCLE_ENTRY_SIZE (2 + LF_MAX_TAG_ID_SIZE)
#define LF_CYCLE_SIZE 152

// Emulator data size of an LF tag type; 0 if the type is not emulated.
uint16_t lf_tag_id_size(tag_specific_type_t type);
bool lf_cycle_valid(const uint8_t *data, uint16_t length);
// Validates the complete list before upgrading v4 in place for the next flash save.
bool lf_cycle_prepare_save(uint8_t *data, uint16_t length);
