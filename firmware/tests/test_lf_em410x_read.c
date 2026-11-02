// Host regression test: real reader, codecs and T55xx frame encoders; only RF
// input and the millisecond timer are replaced with a Manchester signal source.
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "bsp_time.h"
#include "circular_buffer.h"
#include "lf_reader_data.h"
#include "protocols/em410x.h"

static uint16_t intervals[2048];
static size_t interval_count, interval_index;
static uint32_t elapsed_cycles;
static autotimer timer;

static void signal_from_words(const uint32_t *words, unsigned bits, unsigned clock, unsigned phase) {
    interval_count = interval_index = elapsed_cycles = 0;
    unsigned last_fall = 0;
    bool have_fall = false;
    int previous = -1;
    // Four repetitions allow acquisition at any point within either frame.
    for (unsigned half = 0; half < bits * 2 * 4; half++) {
        unsigned position = (half + phase * 2) % (bits * 2);
        unsigned bit = position / 2;
        int level = 1 ^ ((words[bit / 32] >> (31 - bit % 32)) & 1) ^ (position & 1);
        if (previous == 1 && level == 0) {
            unsigned now = half * clock / 2;
            if (have_fall) intervals[interval_count++] = now - last_fall;
            last_fall = now;
            have_fall = true;
        }
        previous = level;
    }
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
autotimer *bsp_obtain_timer(uint32_t initial) { timer.time = initial; return &timer; }
void bsp_return_timer(autotimer *value) { (void)value; }
void register_rio_callback(RIO_CALLBACK_S callback) { (void)callback; }
void unregister_rio_callback(void) {}
uint32_t get_lf_counter_value(void) { return 0; }
void clear_lf_counter_value(void) {}
void start_lf_125khz_radio(void) {}
void stop_lf_125khz_radio(void) {}
void lf_125khz_radio_gpiote_enable(void) {}
void lf_125khz_radio_gpiote_disable(void) {}

static void expect_read(const uint8_t *id, unsigned id_size, unsigned type, unsigned clock, unsigned phase) {
    uint32_t blocks[5];
    if (id_size == 13) assert(em410x_electra_t55xx_writer((uint8_t *)id, blocks) == 5);
    else assert(em410x_t55xx_writer((uint8_t *)id, blocks) == 3);
    signal_from_words(blocks + 1, id_size == 13 ? 128 : 64, clock, phase);
    uint8_t readback[15] = {0};
    if (!em410x_read(readback, 500)) {
        fprintf(stderr, "no read: phase=%u clock=%u expected type=%u intervals=%zu time=%u\n", phase, clock, type, interval_count, (unsigned)timer.time);
        for (size_t i = 0; i < em410x_protocols_size; i++) {
            const protocol *p = em410x_protocols[i];
            void *codec = p->alloc();
            p->decoder.start(codec, 0);
            unsigned hits = 0;
            for (size_t j = 0; j < interval_count; j++) hits += p->decoder.feed(codec, intervals[j]);
            fprintf(stderr, "decoder %u hits=%u\n", p->tag_type, hits);
            p->free(codec);
        }
        abort();
    }
    unsigned actual_type = (readback[0] << 8) | readback[1];
    if (actual_type != type || memcmp(readback + 2, id, id_size)) {
        fprintf(stderr, "phase=%u clock=%u expected type=%u actual=%u\n", phase, clock, type, actual_type);
        abort();
    }
}

int main(void) {
    // Synthetic ID; its extension differs by one bit from the encoded prefix.
    const uint8_t electra[13] = {0x12, 0x34, 0x56, 0x78, 0x90, 0xFF, 0x84, 0xA6, 0x4A, 0x98, 0xF8, 0xC8, 0x02};
    for (unsigned phase = 0; phase < 128; phase++)
        expect_read(electra, 13, TAG_TYPE_EM410X_ELECTRA, 64, phase);
    for (unsigned phase = 0; phase < 64; phase++) {
        expect_read(electra, 5, TAG_TYPE_EM410X_64, 64, phase);
        expect_read(electra, 5, TAG_TYPE_EM410X_32, 32, phase);
        expect_read(electra, 5, TAG_TYPE_EM410X_16, 16, phase);
    }
    // Invalid parity must not become a successful short or extended read.
    uint32_t blocks[5];
    em410x_electra_t55xx_writer((uint8_t *)electra, blocks);
    blocks[1] ^= 1u << 12;
    signal_from_words(blocks + 1, 128, 64, 0);
    uint8_t readback[15] = {0};
    assert(!em410x_read(readback, 500));
    interval_count = interval_index = elapsed_cycles = 0;
    assert(!em410x_read(readback, 500));
    assert(!em410x_capture_intervals(intervals, 8, 20, &interval_index));
    em410x_electra_t55xx_writer((uint8_t *)electra, blocks);
    signal_from_words(blocks + 1, 128, 64, 0);
    uint16_t captured[8];
    captured[7] = 0xABCD;
    size_t count;
    assert(em410x_capture_intervals(captured, 7, 350, &count));
    assert(count == 7 && memcmp(captured, intervals, 7 * sizeof(uint16_t)) == 0);
    assert(captured[7] == 0xABCD);
    puts("PASS: Electra at all 128 phases; EM410x RF/16, /32, /64; invalid parity; absent card");
}
