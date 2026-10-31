#pragma once

#include <stdbool.h>

#include "rfid_main.h"
#include "tag_emulation.h"

#define LF_EM410X_TAG_ID_SIZE 5
#define LF_EM410X_ELECTRA_TAG_ID_SIZE 13
#define LF_IOPROX_TAG_ID_SIZE 16
#define LF_HIDPROX_TAG_ID_SIZE 13
#define LF_VIKING_TAG_ID_SIZE 4
#define LF_PAC_TAG_ID_SIZE 8
#define LF_JABLOTRON_TAG_ID_SIZE 5
#define LF_IDTECK_TAG_ID_SIZE 8
#define LF_PYRAMID_TAG_ID_SIZE 16
#define LF_MAX_TAG_ID_SIZE 16
// Card cycle record. The slot's own card stays first for the legacy commands
// (zero-padded to 16 bytes), then marker, version, total count, bursts,
// silence in 100 ms units, three padding bytes and seven extra cards. Each
// extra card is its tag type (u16, big-endian) and 16 data bytes, so cards of
// any LF type can be mixed in one cycle.
#define LF_CYCLE_MAX 8
#define LF_CYCLE_MARKER 16
#define LF_CYCLE_VERSION 17
#define LF_CYCLE_COUNT 18
#define LF_CYCLE_BURSTS 19
#define LF_CYCLE_SILENCE 20
#define LF_CYCLE_EXTRAS 24
#define LF_CYCLE_ENTRY_SIZE (2 + LF_MAX_TAG_ID_SIZE)
#define LF_CYCLE_SIZE 152
bool lf_cycle_valid(const uint8_t *data, uint16_t length);
// Emulator data size of an LF tag type; 0 if the type is not emulated.
uint16_t lf_tag_id_size(tag_specific_type_t type);

void lf_tag_125khz_sense_switch(bool enable);
int lf_tag_data_loadcb(tag_specific_type_t type, tag_data_buffer_t *buffer);
int lf_tag_data_savecb(tag_specific_type_t type, tag_data_buffer_t *buffer);
bool lf_tag_em410x_data_factory(uint8_t slot, tag_specific_type_t tag_type);
bool lf_tag_hidprox_data_factory(uint8_t slot, tag_specific_type_t tag_type);
bool lf_tag_ioprox_data_factory(uint8_t slot, tag_specific_type_t tag_type);
bool lf_tag_viking_data_factory(uint8_t slot, tag_specific_type_t tag_type);
bool lf_tag_pac_data_factory(uint8_t slot, tag_specific_type_t tag_type);
bool lf_tag_jablotron_data_factory(uint8_t slot, tag_specific_type_t tag_type);
bool lf_tag_idteck_data_factory(uint8_t slot, tag_specific_type_t tag_type);
bool lf_tag_pyramid_data_factory(uint8_t slot, tag_specific_type_t tag_type);
bool is_lf_field_exists(void);
