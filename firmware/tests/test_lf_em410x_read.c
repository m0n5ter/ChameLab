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
static RIO_CALLBACK_S edge_callback;
static uint16_t latched_interval, queued_interval;
static uint32_t irq_counter;
static bool capture_enabled, interval_queued;
static uint8_t dma_samples[4096];
static bool interrupt_extension, dma_timeout;
static bool dma_glitches;
static bool corrupt_one_extension;
static unsigned dma_captures;

static void signal_from_words(const uint32_t *words, unsigned bits, unsigned clock, unsigned phase) {
    interval_count = interval_index = elapsed_cycles = 0;
    // Comparator polarity: rising edges correspond to the falling edges below.
    for (unsigned sample = 0; sample < sizeof(dma_samples) * 8; sample++) {
        unsigned position = (sample / clock + phase) % bits;
        unsigned level = ((words[position / 32] >> (31 - position % 32)) & 1) ^
                         ((sample % clock) >= clock / 2);
        unsigned mask = 1u << (7 - sample % 8);
        if (level) dma_samples[sample / 8] |= mask;
        else dma_samples[sample / 8] &= ~mask;
    }
    unsigned last_fall = 0;
    bool have_fall = false;
    int previous = -1;
    // Four repetitions allow acquisition at any point within either frame.
    for (unsigned half = 0; half < bits * 2 * 4; half++) {
        unsigned position = (half + phase * 2) % (bits * 2);
        unsigned bit = position / 2;
        unsigned bit_value = (words[bit / 32] >> (31 - bit % 32)) & 1;
        if (corrupt_one_extension && half / (bits * 2) == 1 && bit == 92) bit_value ^= 1;
        int level = 1 ^ bit_value ^ (position & 1);
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
bool cb_push_back(circular_buffer *cb, const void *item) {
    (void)cb;
    assert(!interval_queued);
    queued_interval = *(const uint16_t *)item;
    interval_queued = true;
    return true;
}
bool cb_pop_front(circular_buffer *cb, void *item) {
    (void)cb;
    if (interval_index == interval_count) { timer.time++; return false; }
    uint16_t value = intervals[interval_index++];
    elapsed_cycles += value;
    timer.time = elapsed_cycles / 125;
    // Model varying BLE ISR latency: the carrier counter keeps advancing
    // after the edge, while the hardware-captured interval stays unchanged.
    latched_interval = value;
    // Repeated missed GPIO edges after the first prefix prevent any complete
    // Electra frame, while DMA still has the continuous comparator waveform.
    if (interrupt_extension && interval_index >= 60 && interval_index % 20 == 0)
        latched_interval = 255;
    irq_counter = value + (interval_index * 7 % (value / 2));
    assert(capture_enabled && edge_callback);
    edge_callback();
    assert(interval_queued);
    *(uint16_t *)item = queued_interval;
    interval_queued = false;
    return true;
}
autotimer *bsp_obtain_timer(uint32_t initial) { timer.time = initial; return &timer; }
void bsp_return_timer(autotimer *value) { (void)value; }
void register_rio_callback(RIO_CALLBACK_S callback) { edge_callback = callback; }
void unregister_rio_callback(void) { edge_callback = NULL; }
uint32_t get_lf_counter_value(void) { return irq_counter; }
void clear_lf_counter_value(void) { assert(!capture_enabled); }
void start_lf_125khz_radio(void) {}
void stop_lf_125khz_radio(void) {}
void lf_125khz_radio_gpiote_enable(void) {}
void lf_125khz_radio_gpiote_capture_enable(void) { capture_enabled = true; }
uint32_t lf_125khz_radio_edge_interval(void) { return latched_interval; }
bool lf_125khz_radio_capture_bits(uint8_t *data, size_t size, uint32_t timeout_ms) {
    assert(!capture_enabled && size == sizeof(dma_samples) && timeout_ms >= 263);
    dma_captures++;
    if (dma_timeout) return false;
    memcpy(data, dma_samples, size);
    // Fresh radio/comparator noise on each capture, up to 10 samples wide.
    if (dma_glitches) {
        for (unsigned sample = 0; sample < size * 8; sample++) {
            if ((sample + dma_captures * 317) % 997 < 10)
                data[sample / 8] ^= 1u << (7 - sample % 8);
        }
    }
    return true;
}
void lf_125khz_radio_gpiote_disable(void) { capture_enabled = false; }

static void expect_read_within(const uint8_t *id, unsigned id_size, unsigned type, unsigned clock, unsigned phase, uint32_t timeout_ms) {
    uint32_t blocks[5];
    if (id_size == 13) assert(em410x_electra_t55xx_writer((uint8_t *)id, blocks) == 5);
    else assert(em410x_t55xx_writer((uint8_t *)id, blocks) == 3);
    signal_from_words(blocks + 1, id_size == 13 ? 128 : 64, clock, phase);
    uint8_t readback[15] = {0};
    if (!em410x_read(readback, timeout_ms)) {
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

static void expect_read(const uint8_t *id, unsigned id_size, unsigned type, unsigned clock, unsigned phase) {
    expect_read_within(id, id_size, type, clock, phase, 500);
}

int main(int argc, char **argv) {
    if (argc == 2) {
        FILE *trace = fopen(argv[1], "rb");
        assert(trace);
        size_t count = fread(dma_samples, 1, sizeof(dma_samples), trace);
        fclose(trace);
        assert(count != 0);
        uint8_t data[15] = {0};
        if (em410x_read(data, 500)) {
            unsigned length = data[1] == TAG_TYPE_EM410X_ELECTRA ? 15 : 7;
            for (unsigned i = 0; i < length; i++) printf("%02X", data[i]);
            puts("");
        } else puts("NO_READ");
        fprintf(stderr, "DMA captures: %u\n", dma_captures);
        extern uint32_t em410x_capture_diagnostics[8];
        fprintf(stderr, "DMA phase=%u frames=%u candidates=%u timeout=%u confirmed=%u\n",
                em410x_capture_diagnostics[4], em410x_capture_diagnostics[2],
                em410x_capture_diagnostics[3], em410x_capture_diagnostics[5], em410x_capture_diagnostics[7]);
        return 0;
    }
    // Synthetic ID; its extension differs by one bit from the encoded prefix.
    const uint8_t electra[13] = {0x12, 0x34, 0x56, 0x78, 0x90, 0xFF, 0x84, 0xA6, 0x4A, 0x98, 0xF8, 0xC8, 0x02};
    for (unsigned phase = 0; phase < 128; phase++)
        expect_read(electra, 13, TAG_TYPE_EM410X_ELECTRA, 64, phase);
    // The LF auto scan gives the reader a short timeout: an EM410x frame found
    // inside it keeps the reader listening for the Electra extension.
    for (unsigned phase = 0; phase < 128; phase++)
        expect_read_within(electra, 13, TAG_TYPE_EM410X_ELECTRA, 64, phase, 110);
    // A valid Manchester frame with one wrong extension bit still has valid
    // EM410x prefix parity. It must not be accepted without a matching repeat.
    corrupt_one_extension = true;
    for (unsigned phase = 0; phase < 128; phase++)
        expect_read(electra, 13, TAG_TYPE_EM410X_ELECTRA, 64, phase);
    corrupt_one_extension = false;
    interrupt_extension = true;
    dma_glitches = true;
    unsigned captures_before = dma_captures;
    for (unsigned phase = 0; phase < 128; phase++)
        expect_read(electra, 13, TAG_TYPE_EM410X_ELECTRA, 64, phase);
    assert(dma_captures > captures_before);
    interrupt_extension = false;
    dma_glitches = false;
    for (unsigned phase = 0; phase < 64; phase++) {
        expect_read(electra, 5, TAG_TYPE_EM410X_64, 64, phase);
        expect_read(electra, 5, TAG_TYPE_EM410X_32, 32, phase);
        expect_read(electra, 5, TAG_TYPE_EM410X_16, 16, phase);
    }
    dma_timeout = true;
    expect_read(electra, 5, TAG_TYPE_EM410X_64, 64, 0);
    dma_timeout = false;
    // Invalid parity must not become a successful short or extended read.
    uint32_t blocks[5];
    em410x_electra_t55xx_writer((uint8_t *)electra, blocks);
    blocks[1] ^= 1u << 12;
    signal_from_words(blocks + 1, 128, 64, 0);
    uint8_t readback[15] = {0};
    assert(!em410x_read(readback, 500));
    interval_count = interval_index = elapsed_cycles = 0;
    memset(dma_samples, 0, sizeof(dma_samples));
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
    puts("PASS: Electra at all 128 phases; short timeout; delayed/missed edge ISR with DMA fallback; EM410x RF/16, /32, /64; DMA timeout; invalid parity; absent card");
}
