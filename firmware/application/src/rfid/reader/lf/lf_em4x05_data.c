/*
 * EM4205 / EM4305 uplink: READ word and tag scan.
 *
 * The tag answers a READ with the preamble 00001010 and a 45-bit word in its
 * configured modulation. Manchester at RF/64 (EM410x clones) and RF/32 is
 * decoded, with the same envelope polarity as the EM410x reader: the answer
 * comes from the same modulator as the tag's default read.
 */
#include "lf_em4x05_data.h"

#include <string.h>

#include "app_status.h"
#include "bsp_delay.h"
#include "bsp_time.h"
#include "circular_buffer.h"
#include "lf_125khz_radio.h"
#include "lf_reader_data.h"

#include "utils/manchester.h"

#define NRF_LOG_MODULE_NAME lf_em4x05
#include "nrf_log.h"
#include "nrf_log_ctrl.h"
#include "nrf_log_default_backends.h"
NRF_LOG_MODULE_REGISTER();

#define EM4X05_CB_SIZE        256
/* Preamble is 00001010. The edge that ends the silence before it only
 * re-synchronises the decoder, so its first bit never comes out: match the
 * last 6 bits, which still leaves 13 parity checks behind it. */
#define EM4X05_PREAMBLE       0x0A
#define EM4X05_PREAMBLE_BITS  6
#define EM4X05_WORD_BITS      45
#define EM4X05_ANSWER_BITS    (EM4X05_PREAMBLE_BITS + EM4X05_WORD_BITS)
#define EM4X05_READ_TIMEOUT   80   /* ms: 53 bits at RF/64 take ~27 ms */
#define EM4X05_READ_ATTEMPTS  3

/* Falling-edge intervals in carrier cycles: 1T, 1.5T, 2T of the bit period. */
static uint8_t period_with(uint8_t interval, uint8_t t1, uint8_t jitter) {
    uint8_t t15 = t1 + t1 / 2, t2 = t1 * 2;
    if (interval >= t1 - jitter && interval <= t1 + jitter) return 0;
    if (interval >= t15 - jitter && interval <= t15 + jitter) return 1;
    if (interval >= t2 - jitter && interval <= t2 + jitter) return 2;
    return 3;
}

static uint8_t period_rf64(uint8_t interval) { return period_with(interval, 64, 16); }
static uint8_t period_rf32(uint8_t interval) { return period_with(interval, 32, 8); }

/* Parse the 45 bits after the preamble; window bit 44 is the first data bit. */
static bool em4x05_parse_word(uint64_t window, uint32_t *word) {
    uint32_t w = 0;
    uint8_t col = 0;
    int pos = EM4X05_WORD_BITS - 1;
    for (uint8_t byte = 0; byte < 4; byte++) {
        uint8_t row = 0;
        for (uint8_t i = 0; i < 8; i++) {
            uint8_t b = (window >> pos--) & 1;
            w |= (uint32_t)b << (byte * 8 + i);
            row ^= b;
            col ^= (uint8_t)(b << i);
        }
        if (((window >> pos--) & 1) != row) return false;
    }
    for (uint8_t i = 0; i < 8; i++) {
        if (((window >> pos--) & 1) != ((col >> i) & 1)) return false;
    }
    if ((window >> pos) & 1) return false;  // stop bit
    *word = w;
    return true;
}

static bool em4x05_match(uint64_t window, uint32_t *word) {
    const uint64_t mask = (1ULL << EM4X05_ANSWER_BITS) - 1;
    window &= mask;
    return ((window >> EM4X05_WORD_BITS) & ((1u << EM4X05_PREAMBLE_BITS) - 1)) == EM4X05_PREAMBLE &&
           em4x05_parse_word(window, word);
}

typedef struct {
    manchester modem;
    uint64_t window;
    uint8_t count;
} em4x05_rx_t;

static bool em4x05_rx_feed(em4x05_rx_t *rx, uint8_t interval, uint32_t *word) {
    bool bits[2];
    int8_t len = 0;
    manchester_feed(&rx->modem, interval, bits, &len);
    if (len < 0) {
        manchester_reset(&rx->modem);
        rx->count = 0;
        return false;
    }
    for (int8_t i = 0; i < len; i++) {
        rx->window = (rx->window << 1) | (bits[i] ? 1 : 0);
        if (rx->count < EM4X05_ANSWER_BITS) rx->count++;
        if (rx->count == EM4X05_ANSWER_BITS && em4x05_match(rx->window, word)) {
            return true;
        }
    }
    return false;
}

static circular_buffer g_cb;

static void em4x05_edge_cb(void) {
    uint32_t cnt = get_lf_counter_value();
    uint16_t val = (cnt > 0xff) ? 0xff : (uint16_t)cnt;
    cb_push_back(&g_cb, &val);
    clear_lf_counter_value();
}

static bool em4x05_read_word_once(uint8_t addr, uint32_t *word, uint32_t timeout_ms) {
    if (!cb_init(&g_cb, EM4X05_CB_SIZE, sizeof(uint16_t))) {
        return false;
    }
    em4x05_send_read(addr);

    register_rio_callback(em4x05_edge_cb);
    lf_125khz_radio_gpiote_enable();
    clear_lf_counter_value();

    em4x05_rx_t rx[2] = {
        {.modem = {.sync = true, .rp = period_rf64}},
        {.modem = {.sync = true, .rp = period_rf32}},
    };
    bool ok = false;
    autotimer *p_at = bsp_obtain_timer(0);
    while (!ok && NO_TIMEOUT_1MS(p_at, timeout_ms)) {
        uint16_t interval = 0;
        if (!cb_pop_front(&g_cb, &interval)) {
            continue;
        }
        for (int i = 0; i < 2 && !ok; i++) {
            ok = em4x05_rx_feed(&rx[i], (uint8_t)interval, word);
        }
    }
    bsp_return_timer(p_at);
    lf_125khz_radio_gpiote_disable();
    unregister_rio_callback();
    cb_free(&g_cb);
    return ok;
}

bool em4x05_read_word(uint8_t addr, uint32_t *word, uint32_t timeout_ms) {
    for (int i = 0; i < EM4X05_READ_ATTEMPTS; i++) {
        if (em4x05_read_word_once(addr, word, timeout_ms)) {
            return true;
        }
    }
    return false;
}

bool em4x05_read(em4x05_data_t *out, uint32_t timeout_ms) {
    uint32_t password = out->password;
    bool use_password = out->use_password;
    memset(out, 0, sizeof(*out));
    out->password = password;
    out->use_password = use_password;

    if (use_password) {
        em4x05_send_login(password);
        bsp_delay_ms(5);
    }
    if (!em4x05_read_word(EM4X05_BLOCK_UID, &out->uid, timeout_ms)) {
        NRF_LOG_DEBUG("em4x05: UID read failed");
        return false;
    }
    out->uid_block = EM4X05_BLOCK_UID;
    if (!em4x05_read_word(EM4X05_BLOCK_CONFIG, &out->config, timeout_ms)) {
        // The UID answers while the configuration word stays silent: the tag
        // wants a LOGIN (read protection) we did not or could not provide.
        out->login_required = true;
        return false;
    }
    return true;
}

uint8_t scan_em4x05(em4x05_data_t *out) {
    start_lf_125khz_radio();
    bsp_delay_ms(10);

    bool found = em4x05_read(out, EM4X05_READ_TIMEOUT);

    stop_lf_125khz_radio();

    if (!found && out->login_required) {
        return STATUS_LF_TAG_LOGIN_REQUIRED;
    }
    return found ? STATUS_LF_TAG_OK : STATUS_LF_TAG_NO_FOUND;
}
