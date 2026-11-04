// Host regression test for EM4205/EM4305 support. The real downlink encoder,
// EM4x05 reader and EM410x reader run against a strict tag model: every field
// gap the firmware produces is decoded back into datasheet bits, and the model
// answers with a Manchester signal fed through the real decoders.
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "app_status.h"
#include "bsp_time.h"
#include "circular_buffer.h"
#include "lf_em4x05_data.h"
#include "lf_reader_data.h"
#include "protocols/em410x.h"
#include "timeslot.h"

/* ---------------------------------------------------------------- model */

static struct {
    uint32_t mem[16];
    bool logged_in;
    bool corrupt;        // flip one data bit of every READ answer
    unsigned writes, logins, reads;
} tag;

static bool field_on, in_slot;
static struct { bool on; uint32_t us; } events[512];
static unsigned event_count;

static uint16_t intervals[8192];
static size_t interval_count, interval_index;
static uint32_t elapsed_cycles;
static autotimer timer;

static unsigned rf_of(uint32_t cfg) { return ((cfg & 0x3F) + 1) * 2; }
static bool manchester_of(uint32_t cfg) { return ((cfg >> 6) & 0xF) == 1; }

// Manchester signal, same envelope convention as test_lf_em410x_read.c.
static void signal_from_bits(const uint8_t *bits, unsigned count, unsigned repeat, unsigned clock) {
    interval_count = interval_index = elapsed_cycles = 0;
    unsigned last_fall = 0;
    bool have_fall = false;
    int previous = -1;
    for (unsigned half = 0; half < count * 2 * repeat; half++) {
        unsigned position = half % (count * 2);
        int level = 1 ^ bits[position / 2] ^ (position & 1);
        if (previous == 1 && level == 0) {
            unsigned now = half * clock / 2;
            if (have_fall) {
                assert(interval_count < sizeof(intervals) / sizeof(intervals[0]));
                intervals[interval_count++] = now - last_fall;
            }
            last_fall = now;
            have_fall = true;
        }
        previous = level;
    }
}

static unsigned word_bits(uint32_t word, uint8_t *bits) {
    unsigned n = 0;
    uint8_t col = 0;
    for (unsigned byte = 0; byte < 4; byte++) {
        uint8_t row = 0;
        for (unsigned i = 0; i < 8; i++) {
            uint8_t b = (word >> (byte * 8 + i)) & 1;
            bits[n++] = b;
            row ^= b;
            col ^= b << i;
        }
        bits[n++] = row;
    }
    for (unsigned i = 0; i < 8; i++) bits[n++] = (col >> i) & 1;
    bits[n++] = 0;
    return n;
}

static void answer_read(uint32_t word) {
    uint32_t cfg = tag.mem[EM4X05_BLOCK_CONFIG];
    if (!manchester_of(cfg)) { interval_count = interval_index = 0; return; }  // undecodable for us
    // The tag is silent between the command and its answer.
    uint8_t bits[8 + 45];
    unsigned n = 0;
    const uint8_t preamble[8] = {0, 0, 0, 0, 1, 0, 1, 0};
    memcpy(bits + n, preamble, 8);
    n += 8;
    n += word_bits(word, bits + n);
    if (tag.corrupt) bits[8 + 3] ^= 1;
    signal_from_bits(bits, n, 1, rf_of(cfg));
}

// Default read: blocks 5..LWR, each LSB first, repeated.
static void default_read(void) {
    uint32_t cfg = tag.mem[EM4X05_BLOCK_CONFIG];
    unsigned lwr = EM4X05_CFG_GET_LWR(cfg);
    if (!manchester_of(cfg) || lwr < 5) { interval_count = interval_index = 0; return; }
    uint8_t bits[32 * 11];
    unsigned n = 0;
    for (unsigned b = 5; b <= lwr; b++)
        for (unsigned i = 0; i < 32; i++) bits[n++] = (tag.mem[b] >> i) & 1;
    signal_from_bits(bits, n, 4, rf_of(cfg));
}

static unsigned take(const uint8_t *bits, unsigned *pos, unsigned count) {
    unsigned v = 0;
    for (unsigned i = 0; i < count; i++) v |= (unsigned)bits[(*pos)++] << i;
    return v;
}

static bool parse_word(const uint8_t *bits, unsigned *pos, uint32_t *word) {
    uint32_t w = 0;
    uint8_t col = 0;
    for (unsigned byte = 0; byte < 4; byte++) {
        unsigned v = take(bits, pos, 8);
        uint8_t row = __builtin_popcount(v) & 1;
        if (bits[(*pos)++] != row) return false;
        w |= (uint32_t)v << (byte * 8);
        col ^= v;
    }
    if (take(bits, pos, 8) != col) return false;
    if (bits[(*pos)++] != 0) return false;
    *word = w;
    return true;
}

static void tag_receive(const uint8_t *bits, unsigned n) {
    unsigned pos = 0;
    assert(n >= 6 && bits[0] == 0 && bits[1] == 0);
    pos = 2;
    unsigned cmd = take(bits, &pos, 4);
    assert(((cmd ^ (cmd >> 1) ^ (cmd >> 2) ^ (cmd >> 3)) & 1) == 0);  // even parity
    uint32_t word = 0;
    if (cmd == EM4X05_CMD_LOGIN) {
        assert(n == 6 + 45 && parse_word(bits, &pos, &word));
        tag.logins++;
        tag.logged_in = word == tag.mem[EM4X05_BLOCK_PASSWD];
        return;
    }
    assert(cmd == EM4X05_CMD_READ || cmd == EM4X05_CMD_WRITE);
    unsigned addr = take(bits, &pos, 6);
    assert(addr < 16);
    assert(bits[pos++] == (__builtin_popcount(addr) & 1));
    bool read_locked = (tag.mem[EM4X05_BLOCK_CONFIG] & EM4X05_CFG_READ_LOGIN) && !tag.logged_in;
    if (cmd == EM4X05_CMD_READ) {
        assert(n == 13);
        tag.reads++;
        if (addr == EM4X05_BLOCK_PASSWD || (read_locked && addr != EM4X05_BLOCK_UID)) {
            interval_count = interval_index = 0;
        } else {
            answer_read(tag.mem[addr]);
        }
        return;
    }
    assert(n == 13 + 45 && parse_word(bits, &pos, &word));
    if ((tag.mem[EM4X05_BLOCK_CONFIG] & EM4X05_CFG_WRITE_LOGIN) && !tag.logged_in) return;
    if (addr == EM4X05_BLOCK_UID || addr == EM4X05_BLOCK_INFO) return;  // factory-locked
    tag.mem[addr] = word;
    tag.writes++;
}

/* ---------------------------------------------------------------- stubs */

void request_timeslot(uint32_t duration, timeslot_callback_t callback) {
    assert(field_on);
    event_count = 0;
    in_slot = true;
    callback();
    in_slot = false;
    assert(field_on);
    // Decode: start gap (first '0'), then '1' = 32 Tc on, '0' = 23 Tc off + 18 Tc on.
    uint8_t bits[EM4X05_FRAME_MAX_BITS];
    unsigned n = 0, i = 0;
    uint32_t total = 0;
    for (unsigned k = 0; k < event_count; k++) total += events[k].us;
    assert(total <= duration);
    assert(event_count >= 2 && !events[0].on && events[0].us == EM4X05_START_GAP_US);
    assert(events[1].on && events[1].us == EM4X05_GAP_ON_US);
    bits[n++] = 0;
    for (i = 2; i < event_count;) {
        if (events[i].on) {
            assert(events[i].us == EM4X05_BIT_ONE_US);
            bits[n++] = 1;
            i++;
        } else {
            assert(events[i].us == EM4X05_GAP_US && i + 1 < event_count);
            assert(events[i + 1].on && events[i + 1].us == EM4X05_GAP_ON_US);
            bits[n++] = 0;
            i += 2;
        }
        assert(n <= sizeof(bits));
    }
    tag_receive(bits, n);
}

void bsp_delay_us(uint32_t us) {
    if (!in_slot) return;
    assert(event_count < sizeof(events) / sizeof(events[0]));
    events[event_count].on = field_on;
    events[event_count].us = us;
    event_count++;
}
void bsp_delay_ms(uint16_t ms) { (void)ms; }

void start_lf_125khz_radio(void) {
    if (!in_slot && !field_on) {  // power-on reset
        tag.logged_in = false;
        default_read();
    }
    field_on = true;
}
void stop_lf_125khz_radio(void) {
    field_on = false;
    if (!in_slot) tag.logged_in = false;
}

bool cb_init(circular_buffer *cb, size_t capacity, size_t size) { (void)cb; (void)capacity; (void)size; return true; }
void cb_free(circular_buffer *cb) { (void)cb; }
bool cb_push_back(circular_buffer *cb, const void *item) { (void)cb; (void)item; return true; }
bool cb_pop_front(circular_buffer *cb, void *item) {
    (void)cb;
    if (interval_index == interval_count) { timer.time++; return false; }
    uint16_t value = intervals[interval_index++];
    elapsed_cycles += value;
    timer.time = elapsed_cycles / 125;
    *(uint16_t *)item = value;
    return true;
}
autotimer *bsp_obtain_timer(uint32_t initial) { timer.time = initial; elapsed_cycles = 0; return &timer; }
void bsp_return_timer(autotimer *value) { (void)value; }
void register_rio_callback(RIO_CALLBACK_S callback) { (void)callback; }
void unregister_rio_callback(void) {}
uint32_t get_lf_counter_value(void) { return 0; }
void clear_lf_counter_value(void) {}
void lf_125khz_radio_gpiote_enable(void) {}
void lf_125khz_radio_gpiote_disable(void) {}

/* ---------------------------------------------------------------- tests */

#define BLANK_CONFIG 0x0002008Fu  // factory EM4305: bi-phase, RF/32, LWR 8

static void blank_tag(uint32_t config, uint32_t password) {
    memset(&tag, 0, sizeof(tag));
    tag.mem[EM4X05_BLOCK_UID] = 0xA1B2C3D4;
    tag.mem[EM4X05_BLOCK_PASSWD] = password;
    tag.mem[EM4X05_BLOCK_CONFIG] = config;
    field_on = false;
}

static void expect_em410x_clone(const uint8_t *id) {
    uint32_t cfg, b5, b6;
    em4x05_em410x_blocks(id, &cfg, &b5, &b6);
    assert(cfg == 0x0001805F);  // same word Proxmark3 writes for EM410x on EM4305
    assert(tag.mem[4] == cfg && tag.mem[5] == b5 && tag.mem[6] == b6);
    // Default read output must be exactly the EM410x frame, header first.
    uint64_t raw = em410x_raw_data((uint8_t *)id), on_air = 0;
    for (unsigned b = 5; b <= 6; b++)
        for (unsigned i = 0; i < 32; i++) on_air = (on_air << 1) | ((tag.mem[b] >> i) & 1);
    assert(on_air == raw);
}

int main(void) {
    const uint8_t id[5] = {0x12, 0x34, 0x56, 0x78, 0x9A};

    // Frame layout matches the datasheet / Proxmark3 bit for bit.
    uint8_t bits[EM4X05_FRAME_MAX_BITS];
    assert(em4x05_build_frame(EM4X05_CMD_READ, 4, true, 0, false, bits) == 13);
    const uint8_t read4[13] = {0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 1};
    assert(memcmp(bits, read4, sizeof(read4)) == 0);
    assert(em4x05_build_frame(EM4X05_CMD_WRITE, 5, true, 0x0001805F, true, bits) == 58);
    assert(em4x05_build_frame(EM4X05_CMD_LOGIN, 0, false, 0x12345678, true, bits) == 51);

    // 1. Factory-blank EM4305 (bi-phase RF/32, unreadable by our decoder).
    blank_tag(BLANK_CONFIG, 0);
    assert(write_em410x_to_em4x05(id, 0, false) == STATUS_LF_TAG_OK);
    expect_em410x_clone(id);
    assert(tag.writes == 3 && tag.logins == 0);
    uint8_t readback[15] = {0};
    field_on = false;
    assert(em410x_read(readback, 500) && memcmp(readback + 2, id, 5) == 0);

    // 2. Re-clone over an existing EM410x clone.
    const uint8_t id2[5] = {0x01, 0x00, 0xFF, 0x80, 0x7E};
    tag.writes = 0;
    assert(write_em410x_to_em4x05(id2, 0, false) == STATUS_LF_TAG_OK);
    expect_em410x_clone(id2);

    // 3. Write-protected tag: refused without the password, works with it.
    blank_tag(BLANK_CONFIG | EM4X05_CFG_WRITE_LOGIN, 0xCAFEBABE);
    assert(write_em410x_to_em4x05(id, 0, false) == STATUS_CMD_ERR);
    assert(tag.writes == 0 && tag.mem[4] == (BLANK_CONFIG | EM4X05_CFG_WRITE_LOGIN));
    assert(write_em410x_to_em4x05(id, 0x11111111, true) == STATUS_CMD_ERR);
    assert(tag.writes == 0);
    assert(write_em410x_to_em4x05(id, 0xCAFEBABE, true) == STATUS_LF_TAG_OK);
    expect_em410x_clone(id);

    // 4. Raw word write.
    assert(em4x05_write_word(16, 0, 0, false) == STATUS_PAR_ERR);
    assert(em4x05_write_word(7, 0xDEADBEEF, 0, false) == STATUS_LF_TAG_OK);
    assert(tag.mem[7] == 0xDEADBEEF);

    // 5. Scan: UID + config at RF/64 and RF/32.
    const uint32_t configs[2] = {0x0001805F, EM4X05_CFG_BITRATE(32) | EM4X05_CFG_MANCHESTER | EM4X05_CFG_LWR(6)};
    for (unsigned c = 0; c < 2; c++) {
        blank_tag(configs[c], 0);
        em4x05_data_t out = {0};
        assert(scan_em4x05(&out) == STATUS_LF_TAG_OK);
        assert(out.uid == 0xA1B2C3D4 && out.config == configs[c] && out.uid_block == EM4X05_BLOCK_UID);
    }

    // 6. Corrupted answers are rejected, absent tags time out.
    blank_tag(0x0001805F, 0);
    tag.corrupt = true;
    em4x05_data_t out = {0};
    assert(scan_em4x05(&out) == STATUS_LF_TAG_NO_FOUND);
    blank_tag(BLANK_CONFIG, 0);  // bi-phase: no decodable answer
    assert(scan_em4x05(&out) == STATUS_LF_TAG_NO_FOUND);

    // 7. Read protection: LOGIN with the right password unlocks the config word.
    blank_tag(0x0001805F | EM4X05_CFG_READ_LOGIN, 0x0BADF00D);
    memset(&out, 0, sizeof(out));
    assert(scan_em4x05(&out) == STATUS_LF_TAG_LOGIN_REQUIRED);
    memset(&out, 0, sizeof(out));
    out.password = 0x0BADF00D;
    out.use_password = true;
    assert(scan_em4x05(&out) == STATUS_LF_TAG_OK && out.config == (0x0001805F | EM4X05_CFG_READ_LOGIN));

    puts("PASS: EM4x05 framing, EM410x clone to EM4305 (blank, re-clone, write login), raw write, scan");
    return 0;
}
