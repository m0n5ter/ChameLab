#ifndef __CRC_UTILS_H
#define __CRC_UTILS_H

#include <stdint.h>

void calc_14a_crc_lut(uint8_t *data, int length, uint8_t *output);

/* Streaming variant of calc_14a_crc_lut */
uint16_t crc_14a_init(void);
uint16_t crc_14a_update(uint16_t crc, uint8_t data);

#endif
