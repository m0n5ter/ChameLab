// Exercise the real LF acquisition callbacks with signed SAADC samples. Only
// RF, the queue and the timer are mocked; protocol decoding stays unchanged.
#include <assert.h>
#include <stdio.h>
#include <string.h>

#include "bsp_time.h"
#include "circular_buffer.h"
#include "lf_125khz_radio.h"
#include "lf_reader_data.h"
#include "protocols/hidprox.h"
#include "protocols/ioprox.h"
#include "protocols/pyramid.h"
#include "protocols/idteck.h"
#include "protocols/pac.h"

static int16_t signal[64 * 128 * 6];
static size_t signal_length, signal_position;
static uint16_t queue[2048];
static size_t queued, queue_position;
static unsigned wrapped, negatives;
static autotimer timer;
static lf_adc_callback_t adc_callback;

bool cb_init(circular_buffer *cb, size_t capacity, size_t size) {
    (void)cb; (void)capacity;
    assert(size == sizeof(uint16_t));
    queued = queue_position = signal_position = wrapped = negatives = 0;
    return true;
}
void cb_free(circular_buffer *cb) { (void)cb; }
bool cb_push_back(circular_buffer *cb, const void *item) {
    (void)cb;
    uint16_t sample;
    memcpy(&sample, item, sizeof(sample));
    if (sample > 16383) wrapped++;
    assert(queued < sizeof(queue) / sizeof(queue[0]));
    queue[queued++] = sample;
    return true;
}
bool cb_pop_front(circular_buffer *cb, void *item) {
    (void)cb;
    if (queue_position == queued && adc_callback && signal_position < signal_length) {
        queued = queue_position = 0;
        size_t count = signal_length - signal_position;
        if (count > 2048) count = 2048;
        for (size_t i = 0; i < count; i++)
            if (signal[signal_position + i] < 0) negatives++;
        adc_callback(signal + signal_position, count);
        signal_position += count;
        timer.time = signal_position / 125;
    }
    if (queue_position == queued) {
        if (adc_callback) timer.time++;
        return false;
    }
    memcpy(item, &queue[queue_position++], sizeof(uint16_t));
    return true;
}
autotimer *bsp_obtain_timer(uint32_t initial) { timer.time = initial; return &timer; }
void bsp_return_timer(autotimer *value) { (void)value; }
void bsp_delay_ms(uint16_t ms) { (void)ms; }
void bsp_wdt_feed(void) {}
void start_lf_125khz_radio(void) {}
void stop_lf_125khz_radio(void) {}
void lf_125khz_radio_saadc_enable(lf_adc_callback_t callback) { adc_callback = callback; }
void lf_125khz_radio_saadc_capture_enable(lf_adc_callback_t callback, nrf_saadc_value_t *buffer, uint16_t count) {
    assert(count == 3 * 128 * 32 && count <= signal_length);
    memcpy(buffer, signal, count * sizeof(*buffer));
    timer.time += (count + 124) / 125;
    callback(buffer, count);
}
void lf_125khz_radio_saadc_disable(void) { adc_callback = NULL; }

static void synthesize(const uint8_t *bits, unsigned count, unsigned bitrate) {
    signal_length = 0;
    unsigned emitted = 0;
    for (unsigned frame = 0; frame < 6; frame++) {
        for (unsigned bit = 0; bit < count; bit++) {
            unsigned period = bits[bit] ? 10 : 8;
            unsigned boundary = (frame * count + bit + 1) * bitrate;
            while (emitted < boundary) {
                for (unsigned t = 0; t < period; t++) {
                    // Noise takes some low samples just below ground. A cast
                    // to unsigned would turn them into spikes near 65535.
                    int16_t sample = t < period / 2 ? 8000 : (int)(signal_length % 7) - 2;
                    assert(signal_length < sizeof(signal) / sizeof(signal[0]));
                    signal[signal_length++] = sample;
                }
                emitted += period;
            }
        }
    }
}

static void check(const char *name, bool read, const uint8_t *actual,
                  const uint8_t *expected, size_t size) {
    printf("%s: decoded=%u, negative ADC samples=%u, wrapped samples=%u\n",
           name, read, negatives, wrapped);
    assert(negatives > 0 && wrapped == 0);
    assert(read && memcmp(actual, expected, size) == 0);
}

int main(int argc, char **argv) {
    setvbuf(stdout, NULL, _IONBF, 0);
    uint8_t bits[128], actual[16];
    const uint8_t pyramid_frame[16] = {0x00, 0x01, 0x01, 0x4A, 0x3E, 0x01, 0x01, 0x01,
                                      0x01, 0x01, 0x01, 0x6B, 0xE3, 0x8C, 0xD6, 0x4A};
    for (unsigned i = 0; i < 128; i++) bits[i] = (pyramid_frame[i / 8] >> (7 - i % 8)) & 1;
    synthesize(bits, 128, 50);
    bool read = pyramid_read(actual, 500);
    check("Pyramid", read, actual, pyramid_frame, sizeof(pyramid_frame));

    wiegand_card_t card = {.format = H10301, .facility_code = 123, .card_number = 45678};
    uint32_t blocks[4];
    assert(hidprox_t55xx_writer(&card, blocks) == 4);
    for (unsigned i = 0; i < 96; i++) bits[i] = (blocks[1 + i / 32] >> (31 - i % 32)) & 1;
    const uint8_t hid_id[11] = {H10301, 0, 0, 0, 123, 0, 0, 0, 0xB2, 0x6E, 0};
    synthesize(bits, 96, 50);
    read = hidprox_read(actual, H10301, 500);
    check("HID Prox", read, actual, hid_id, sizeof(hid_id));

    uint8_t io_id[16];
    assert(ioprox_encode_params_to_data(1, 2, 3456, io_id));
    for (unsigned i = 0; i < 64; i++) bits[i] = (io_id[4 + i / 8] >> (7 - i % 8)) & 1;
    synthesize(bits, 64, 64);
    read = ioprox_read(actual, 0, 500);
    check("ioProx", read, actual, io_id, sizeof(io_id));

    const uint8_t idteck_id[] = {0x49, 0x44, 0x54, 0x4B, 0x35, 0x1F, 0xBE, 0x4B};
    signal_length = 32 * 64 * 4;
    for (unsigned i = 0; i < signal_length; i++) {
        unsigned bit = (i / 32) % 64;
        unsigned value = (idteck_id[bit / 8] >> (7 - bit % 8)) & 1;
        signal[i] = ((i & 1) ^ value) ? 120 : -120;
    }
    assert(idteck_read(actual, 500));
    assert(memcmp(actual, idteck_id, sizeof(idteck_id)) == 0);
    assert(negatives > 0); // Signed PSK phase samples must survive the queue.

    const uint8_t pac_id[8] = "1234AB00";
    uint32_t pac_blocks[5];
    assert(pac_t55xx_writer((uint8_t *)pac_id, pac_blocks) == 5);
    signal_length = 32 * 128 * 4;
    for (unsigned i = 0; i < signal_length; i++) {
        unsigned bit = (i / 32) % 128;
        bool value = (pac_blocks[1 + bit / 32] >> (31 - bit % 32)) & 1;
        signal[i] = (value ? 8000 : 0) + (int)(i * 13 % 31) - 15;
    }
    assert(pac_read(actual, 500));
    assert(memcmp(actual, pac_id, sizeof(pac_id)) == 0);
    uint8_t snapshot[16];
    assert(pac_copy_last_capture(snapshot, 0, sizeof(snapshot)) == sizeof(snapshot));
    assert(memcmp(snapshot, signal, sizeof(snapshot)) == 0);
    assert(pac_copy_last_capture(snapshot, 3 * 128 * 32 * sizeof(int16_t), sizeof(snapshot)) == 0);
    assert(!pac_read(actual, 40)); // A partial DMA period must not outlive its timeout.
    assert(pac_copy_last_capture(snapshot, 0, sizeof(snapshot)) == 0);
    for (int fixture = 1; fixture < argc; fixture++) {
        FILE *input = fopen(argv[fixture], "rb");
        assert(input && fread(signal, sizeof(*signal), 12288, input) == 12288);
        fclose(input);
        signal_length = 12288;
        const char *expected_id = fixture == 3 ? "1234AB01" : fixture == 4 ? "1234AB02" : "1234AB00";
        assert(pac_read(actual, 500));
        assert(memcmp(actual, expected_id, 8) == 0);
    }

    const int16_t raw[] = {-32768, -128, -1, 0, 31, 32, 8159, 8160, 8192, 16383};
    const uint8_t expected[] = {0, 0, 0, 0, 0, 1, 254, 255, 255, 255};
    memcpy(signal, raw, sizeof(raw));
    signal_length = sizeof(raw) / sizeof(raw[0]);
    uint8_t capture[sizeof(expected)];
    size_t captured = 0;
    assert(raw_read_to_buffer(capture, sizeof(capture), 100, &captured));
    assert(captured == sizeof(expected));
    assert(memcmp(capture, expected, sizeof(expected)) == 0);
    assert(wrapped == 0);
    puts("PASS: signed ADC acquisition for Pyramid, HID Prox, ioProx and LF capture");
}
