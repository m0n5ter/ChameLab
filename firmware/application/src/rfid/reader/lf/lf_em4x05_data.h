#pragma once

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* -----------------------------------------------------------------------
 * EM4205 / EM4305 (EM4x05) — reader-talk-first, 16 x 32-bit words.
 *
 * Downlink framing follows the EM4305 datasheet (and the Proxmark3
 * implementation that is known to work with real tags):
 *
 *   command : 0, 0, c0 c1 c2, P              (P = even parity of c0..c2)
 *   address : a0 a1 a2 a3, 0 0, P            (LSB first, even parity)
 *   data    : 4 x (8 data bits LSB first + even row parity)
 *             8 column parity bits (even), 0 stop bit       = 45 bits
 *
 * A word is transmitted least significant bit first, so word bit 0 is the
 * first bit on air.  The tag answers a READ with the preamble 00001010
 * followed by the same 45-bit data layout.
 * --------------------------------------------------------------------- */

/* Command codes as sent on air (c0 first), parity bit included as bit 3. */
#define EM4X05_CMD_LOGIN      0x0C
#define EM4X05_CMD_WRITE      0x0A
#define EM4X05_CMD_READ       0x09
#define EM4X05_CMD_PROTECT    0x03
#define EM4X05_CMD_DISABLE    0x05

/* Memory map (EM4205 / EM4305). */
#define EM4X05_BLOCK_INFO     0
#define EM4X05_BLOCK_UID      1
#define EM4X05_BLOCK_PASSWD   2
#define EM4X05_BLOCK_CONFIG   4
#define EM4X05_BLOCK_DATA     5
#define EM4X05_BLOCK_PROT1    14
#define EM4X05_BLOCK_PROT2    15
#define EM4X05_BLOCK_COUNT    16

/* Configuration word (block 4) fields. */
#define EM4X05_CFG_BITRATE(rf)        ((((rf) - 2) / 2) & 0x3F)  /* RF/(2n+2) */
#define EM4X05_CFG_MANCHESTER         (1u << 6)
#define EM4X05_CFG_BIPHASE            (2u << 6)
#define EM4X05_CFG_LWR(lwr)           (((uint32_t)(lwr) & 0xF) << 14)
#define EM4X05_CFG_GET_LWR(cfg)       (((cfg) >> 14) & 0xF)
#define EM4X05_CFG_READ_LOGIN         (1u << 18)
#define EM4X05_CFG_WRITE_LOGIN        (1u << 20)

/* EM410x emulation: Manchester, RF/64, default read of blocks 5..6. */
#define EM4X05_EM410X_CONFIG  (EM4X05_CFG_BITRATE(64) | EM4X05_CFG_MANCHESTER | EM4X05_CFG_LWR(6))

/* Downlink timing in microseconds (1 Tc = 8 us at 125 kHz). */
#define EM4X05_START_GAP_US   (55 * 8)
#define EM4X05_GAP_US         (23 * 8)   /* field off that encodes a '0'  */
#define EM4X05_GAP_ON_US      (18 * 8)   /* field on after a '0' gap      */
#define EM4X05_BIT_ONE_US     (32 * 8)   /* unmodulated bit period, '1'   */

/* Time the tag needs to program one EEPROM word, with margin. */
#define EM4X05_WRITE_WAIT_MS  15

#define EM4X05_FRAME_MAX_BITS (6 + 7 + 45)

/* -----------------------------------------------------------------------
 * Data structures
 * --------------------------------------------------------------------- */

typedef struct {
    uint32_t config;        /* block 4: configuration word                  */
    uint32_t uid;           /* block 1: device serial number                */
    uint32_t uid_hi;        /* reserved, always 0 (kept for the host API)   */
    bool     is_em4x69;     /* reserved, always false                       */
    uint8_t  uid_block;     /* block number the UID was read from           */
    uint32_t password;      /* password used for LOGIN                      */
    bool     use_password;  /* send LOGIN before reading                    */
    bool     login_required;/* config read failed while UID read worked     */
} em4x05_data_t;

/* -----------------------------------------------------------------------
 * Downlink (lf_em4x05_write.c)
 * --------------------------------------------------------------------- */

/* Build the on-air bit sequence of a command. Returns the number of bits. */
uint8_t em4x05_build_frame(uint8_t cmd, uint8_t addr, bool has_addr, uint32_t data, bool has_data, uint8_t *bits);

/* Transmit a frame. The field must already be on; it is left on. */
void em4x05_send_frame(const uint8_t *bits, uint8_t count);

void em4x05_send_login(uint32_t password);
void em4x05_send_read(uint8_t addr);
void em4x05_send_write(uint8_t addr, uint32_t word);

/* Words for blocks 4..6 that make the tag answer as the given EM410x ID. */
void em4x05_em410x_blocks(const uint8_t *uid, uint32_t *config, uint32_t *blk5, uint32_t *blk6);

/* Raw word write (field handling included). Unverified: read back to check. */
uint8_t em4x05_write_word(uint8_t addr, uint32_t word, uint32_t password, bool use_password);

/* Clone an EM410x ID onto an EM4205/EM4305 and verify it by reading it back. */
uint8_t write_em410x_to_em4x05(const uint8_t *uid, uint32_t password, bool use_password);

/* -----------------------------------------------------------------------
 * Uplink (lf_em4x05_data.c)
 * --------------------------------------------------------------------- */

/* Read one word. The field must already be on. */
bool    em4x05_read_word(uint8_t addr, uint32_t *word, uint32_t timeout_ms);
bool    em4x05_read(em4x05_data_t *out, uint32_t timeout_ms);
uint8_t scan_em4x05(em4x05_data_t *out);

#ifdef __cplusplus
}
#endif
