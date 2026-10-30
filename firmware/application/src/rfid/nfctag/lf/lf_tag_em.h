#pragma once

#include <stdbool.h>

#include "rfid_main.h"
#include "tag_emulation.h"

#define LF_EM410X_TAG_ID_SIZE 5
// First five bytes remain the primary UID for legacy commands. V1 extension:
// magic, version, total count, bursts, seven extra UIDs. V2 appends silence
// in 100 ms units and three padding bytes; v1 records retain zero silence.
#define LF_EM410X_CYCLE_MAX 8
#define LF_EM410X_CYCLE_V1_SIZE 44
#define LF_EM410X_CYCLE_SIZE 48
bool lf_em410x_cycle_valid(const uint8_t *data, uint16_t length);
#define LF_EM410X_ELECTRA_TAG_ID_SIZE 13
#define LF_IOPROX_TAG_ID_SIZE 16
#define LF_HIDPROX_TAG_ID_SIZE 13
#define LF_VIKING_TAG_ID_SIZE 4
#define LF_PAC_TAG_ID_SIZE 8
#define LF_JABLOTRON_TAG_ID_SIZE 5
#define LF_IDTECK_TAG_ID_SIZE 8
#define LF_PYRAMID_TAG_ID_SIZE 16

void lf_tag_125khz_sense_switch(bool enable);
int lf_tag_data_loadcb(tag_specific_type_t type, tag_data_buffer_t *buffer);
int lf_tag_em410x_data_savecb(tag_specific_type_t type, tag_data_buffer_t *buffer);
bool lf_tag_em410x_data_factory(uint8_t slot, tag_specific_type_t tag_type);
int lf_tag_hidprox_data_savecb(tag_specific_type_t type, tag_data_buffer_t *buffer);
bool lf_tag_hidprox_data_factory(uint8_t slot, tag_specific_type_t tag_type);
int lf_tag_ioprox_data_savecb(tag_specific_type_t type, tag_data_buffer_t *buffer);
bool lf_tag_ioprox_data_factory(uint8_t slot, tag_specific_type_t tag_type);
int lf_tag_viking_data_savecb(tag_specific_type_t type, tag_data_buffer_t *buffer);
bool lf_tag_viking_data_factory(uint8_t slot, tag_specific_type_t tag_type);
int lf_tag_pac_data_savecb(tag_specific_type_t type, tag_data_buffer_t *buffer);
bool lf_tag_pac_data_factory(uint8_t slot, tag_specific_type_t tag_type);
int lf_tag_jablotron_data_savecb(tag_specific_type_t type, tag_data_buffer_t *buffer);
bool lf_tag_jablotron_data_factory(uint8_t slot, tag_specific_type_t tag_type);
int lf_tag_idteck_data_savecb(tag_specific_type_t type, tag_data_buffer_t *buffer);
bool lf_tag_idteck_data_factory(uint8_t slot, tag_specific_type_t tag_type);
int lf_tag_pyramid_data_savecb(tag_specific_type_t type, tag_data_buffer_t *buffer);
bool lf_tag_pyramid_data_factory(uint8_t slot, tag_specific_type_t tag_type);
bool is_lf_field_exists(void);
