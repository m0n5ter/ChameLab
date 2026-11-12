#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "mf1_toolbox.h"

enum { STATUS_HF_TAG_OK, STATUS_HF_TAG_NO, STATUS_MF_ERR_AUTH, STATUS_HF_ERR_STAT };
enum { PCD_TRANSCEIVE, RGB_GREEN, RGB_LIST_NUM = 8 };
enum { LATE_NACK, NO_NACK, PARTIAL_NACK, RESYNC_FOREVER, EXPIRE_DURING_SELECTION };
typedef int picc_14a_tag_t;
static picc_14a_tag_t tag, *p_tag_info = &tag;
static uint32_t ticks, bytes_calls, bit_calls, mode, leds[8] = {0,1,2,3,4,5,6,7};
static const uint32_t uid = 0x12345678, nonce = 0xD14191B3;
#define NRF_LOG_INFO(...) ((void)0)
#define NRF_LOG_PROCESS() false
#define U8ARR_BIT_LEN(a) (sizeof(a) * 8)
#define APP_TIMER_TICKS(ms) (ms)
static uint32_t app_timer_cnt_get(void) { return ticks & 0xFFFFFF; }
static uint32_t app_timer_cnt_diff_compute(uint32_t to, uint32_t from) { return (to - from) & 0xFFFFFF; }
static void bsp_wdt_feed(void) { }
static void rgb_marquee_stop(void) { }
static void set_slot_light_color(unsigned color) { }
static uint32_t *hw_get_led_array(void) { return leds; }
static void nrf_gpio_pin_set(unsigned pin) { assert(pin < 8); }
static void nrf_gpio_pin_clear(unsigned pin) { assert(pin < 8); }
static void crc_14a_append(uint8_t *bytes, unsigned length) { }
static void reset_radio_field_with_delay(void) { ticks += mode == EXPIRE_DURING_SELECTION ? 10000 : 150; }
static uint8_t pcd_14a_reader_scan_auto(picc_14a_tag_t *info) { return STATUS_HF_TAG_OK; }
static uint8_t pcd_14a_reader_fast_select(picc_14a_tag_t *info) { return STATUS_HF_TAG_OK; }
static uint32_t get_u32_tag_uid(picc_14a_tag_t *info) { return uid; }
static uint64_t bytes_to_num(const uint8_t *bytes, unsigned count) {
    uint64_t value = 0;
    while (count--) value = (value << 8) | *bytes++;
    return value;
}
static void num_to_bytes(uint64_t value, unsigned count, uint8_t *bytes) {
    while (count) { bytes[--count] = value; value >>= 8; }
}
static void get_4byte_tag_uid(picc_14a_tag_t *info, uint8_t *out) { num_to_bytes(uid, 4, out); }
static uint8_t pcd_14a_reader_bytes_transfer(unsigned cmd, uint8_t *in, unsigned size,
    uint8_t *out, uint16_t *length, unsigned capacity) {
    bytes_calls++;
    // Fourteen failed resyncs followed by one match keep resetting the local
    // retry counter: only the whole-command deadline can bound this case.
    bool miss = mode == RESYNC_FOREVER && bytes_calls > 15 && (bytes_calls - 15) % 15 != 0;
    num_to_bytes(miss ? nonce ^ 1 : nonce, 4, out);
    *length = 32;
    return STATUS_HF_TAG_OK;
}
static void pcd_14a_reader_bits_transfer(uint8_t *in, unsigned size, uint8_t *parity,
    uint8_t *out, uint8_t *out_parity, uint16_t *length, unsigned capacity) {
    bit_calls++;
    unsigned par = 0;
    for (unsigned i=0; i<8; ++i) par = (par << 1) | parity[i];
    unsigned index = in[3] >> 5;
    bool nack = mode == LATE_NACK ? par == 255 : mode == PARTIAL_NACK && index == 0;
    out[0] = 5 ^ index;
    *length = nack ? 4 : 0;
}

// Extracted verbatim from production by test_mifare_recovery.py.
#include "darkside_under_test.c"

static void run(unsigned test_mode, mf1_darkside_status_t expected, uint32_t start) {
    mode = test_mode;
    ticks = start;
    bytes_calls = bit_calls = 0;
    DarksideCore_t data = {0};
    mf1_darkside_status_t result = DARKSIDE_OK;
    assert(darkside_recover_key(3, 0x61, 1, 15, &data, &result) == STATUS_HF_TAG_OK);
    assert(result == expected);
    uint32_t elapsed = ticks - start;
    assert(elapsed <= 100000);
    if (mode == LATE_NACK) {
        // A valid full sweep can exceed the former 60-second host timeout.
        assert(elapsed > 60000 && elapsed < 90000 && bit_calls == 480);
        assert(bytes_to_num(data.uid, 4) == uid && bytes_to_num(data.nt, 4) == nonce);
        for (unsigned i=0; i<8; ++i) assert(data.ks_list[i] == i);
    }
    if (mode == RESYNC_FOREVER || mode == EXPIRE_DURING_SELECTION) assert(elapsed >= 90000);
}

int main(void) {
    run(LATE_NACK, DARKSIDE_OK, 0);
    run(LATE_NACK, DARKSIDE_OK, 0xFFFFFF - 1000); // RTC rollover.
    run(NO_NACK, DARKSIDE_NO_NAK_SENT, 0);
    run(PARTIAL_NACK, DARKSIDE_NO_NAK_SENT, 0);
    run(RESYNC_FOREVER, DARKSIDE_CANT_FIX_NT, 0);
    run(EXPIRE_DURING_SELECTION, DARKSIDE_CANT_FIX_NT, 0);
    puts("Darkside: long valid sweep, partial NACK status, bounded resync and RTC rollover passed.");
}
