/*
 * EM4205 / EM4305 downlink: command framing, LOGIN, WRITE and EM410x cloning.
 *
 * Kept free of the receive path so the host tests can decode exactly the
 * bits this file puts on air (firmware/tests/test_lf_em4x05_write.c).
 */
#include <string.h>

#include "app_status.h"
#include "bsp_delay.h"
#include "lf_125khz_radio.h"
#include "lf_em4x05_data.h"
#include "lf_reader_data.h"
#include "protocols/em410x.h"
#include "timeslot.h"

#define NRF_LOG_MODULE_NAME lf_em4x05_w
#include "nrf_log.h"
#include "nrf_log_ctrl.h"
#include "nrf_log_default_backends.h"
NRF_LOG_MODULE_REGISTER();

#define EM4X05_WRITE_ATTEMPTS  2
#define EM4X05_VERIFY_MS       500

static uint8_t append_data(uint8_t *bits, uint32_t data) {
    uint8_t n = 0;
    uint8_t col = 0;
    for (uint8_t byte = 0; byte < 4; byte++) {
        uint8_t row = 0;
        for (uint8_t i = 0; i < 8; i++) {
            uint8_t b = (data >> (byte * 8 + i)) & 1;
            bits[n++] = b;
            row ^= b;
            col ^= (uint8_t)(b << i);
        }
        bits[n++] = row;
    }
    for (uint8_t i = 0; i < 8; i++) {
        bits[n++] = (col >> i) & 1;
    }
    bits[n++] = 0;  // stop bit
    return n;
}

uint8_t em4x05_build_frame(uint8_t cmd, uint8_t addr, bool has_addr, uint32_t data, bool has_data, uint8_t *bits) {
    uint8_t n = 0;
    bits[n++] = 0;  // start bit, carried by the start gap
    bits[n++] = 0;  // second '0' required by EM4x05
    for (uint8_t i = 0; i < 4; i++) {
        bits[n++] = (cmd >> i) & 1;
    }
    if (has_addr) {
        uint8_t parity = 0;
        for (uint8_t i = 0; i < 6; i++) {
            uint8_t b = (i < 4) ? ((addr >> i) & 1) : 0;
            bits[n++] = b;
            parity ^= b;
        }
        bits[n++] = parity;
    }
    if (has_data) {
        n += append_data(bits + n, data);
    }
    return n;
}

static const uint8_t *m_tx_bits;
static uint8_t m_tx_count;

static void em4x05_tx_timeslot_cb(void) {
    // The start gap stands for the first '0'.
    stop_lf_125khz_radio();
    bsp_delay_us(EM4X05_START_GAP_US);
    start_lf_125khz_radio();
    bsp_delay_us(EM4X05_GAP_ON_US);
    for (uint8_t i = 1; i < m_tx_count; i++) {
        if (m_tx_bits[i]) {
            bsp_delay_us(EM4X05_BIT_ONE_US);
        } else {
            stop_lf_125khz_radio();
            bsp_delay_us(EM4X05_GAP_US);
            start_lf_125khz_radio();
            bsp_delay_us(EM4X05_GAP_ON_US);
        }
    }
}

void em4x05_send_frame(const uint8_t *bits, uint8_t count) {
    m_tx_bits = bits;
    m_tx_count = count;
    // Worst case every bit is a '0' (41 Tc), plus the start gap.
    uint32_t duration = EM4X05_START_GAP_US + (uint32_t)count * (EM4X05_GAP_US + EM4X05_GAP_ON_US) + 1000;
    request_timeslot(duration, em4x05_tx_timeslot_cb);
}

void em4x05_send_login(uint32_t password) {
    uint8_t bits[EM4X05_FRAME_MAX_BITS];
    uint8_t n = em4x05_build_frame(EM4X05_CMD_LOGIN, 0, false, password, true, bits);
    em4x05_send_frame(bits, n);
}

void em4x05_send_read(uint8_t addr) {
    uint8_t bits[EM4X05_FRAME_MAX_BITS];
    uint8_t n = em4x05_build_frame(EM4X05_CMD_READ, addr, true, 0, false, bits);
    em4x05_send_frame(bits, n);
}

void em4x05_send_write(uint8_t addr, uint32_t word) {
    uint8_t bits[EM4X05_FRAME_MAX_BITS];
    uint8_t n = em4x05_build_frame(EM4X05_CMD_WRITE, addr, true, word, true, bits);
    em4x05_send_frame(bits, n);
}

static uint32_t reflect32(uint32_t v) {
    uint32_t r = 0;
    for (uint8_t i = 0; i < 32; i++) {
        r = (r << 1) | ((v >> i) & 1);
    }
    return r;
}

void em4x05_em410x_blocks(const uint8_t *uid, uint32_t *config, uint32_t *blk5, uint32_t *blk6) {
    uint8_t id[5];
    memcpy(id, uid, sizeof(id));
    uint64_t raw = em410x_raw_data(id);
    // Default read sends block 5 then 6, each word LSB first; the EM410x frame
    // is defined MSB (header) first, so each half is bit-reversed.
    *config = EM4X05_EM410X_CONFIG;
    *blk5 = reflect32((uint32_t)(raw >> 32));
    *blk6 = reflect32((uint32_t)raw);
}

static void power_up(void) {
    start_lf_125khz_radio();
    bsp_delay_ms(10);  // let the tag power up and start its default read
}

static void write_one(uint8_t addr, uint32_t word) {
    em4x05_send_write(addr, word);
    bsp_delay_ms(EM4X05_WRITE_WAIT_MS);
}

uint8_t em4x05_write_word(uint8_t addr, uint32_t word, uint32_t password, bool use_password) {
    if (addr >= EM4X05_BLOCK_COUNT) {
        return STATUS_PAR_ERR;
    }
    power_up();
    if (use_password) {
        em4x05_send_login(password);
        bsp_delay_ms(5);
    }
    write_one(addr, word);
    stop_lf_125khz_radio();
    return STATUS_LF_TAG_OK;
}

uint8_t write_em410x_to_em4x05(const uint8_t *uid, uint32_t password, bool use_password) {
    uint32_t config, blk5, blk6;
    em4x05_em410x_blocks(uid, &config, &blk5, &blk6);

    for (uint8_t attempt = 0; attempt < EM4X05_WRITE_ATTEMPTS; attempt++) {
        power_up();
        if (use_password) {
            em4x05_send_login(password);
            bsp_delay_ms(5);
        }
        // Data first, configuration last: the tag keeps answering in its old
        // modulation until the new configuration word is in place.
        write_one(EM4X05_BLOCK_DATA, blk5);
        write_one(EM4X05_BLOCK_DATA + 1, blk6);
        write_one(EM4X05_BLOCK_CONFIG, config);
        stop_lf_125khz_radio();
        bsp_delay_ms(20);  // power cycle so the tag reloads its configuration

        // Verify with the regular EM410x reader: that is what a door reader sees.
        uint8_t read[2 + 13] = {0};
        if (em410x_read(read, EM4X05_VERIFY_MS) && memcmp(read + 2, uid, 5) == 0) {
            return STATUS_LF_TAG_OK;
        }
        NRF_LOG_INFO("em4x05: EM410x verify failed, attempt %d", attempt + 1);
    }
    return STATUS_CMD_ERR;
}
