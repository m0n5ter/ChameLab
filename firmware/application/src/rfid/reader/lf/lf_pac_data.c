#include <string.h>

#include "bsp_delay.h"
#include "bsp_time.h"
#include "lf_125khz_radio.h"
#include "lf_reader_data.h"
#include "protocols/pac.h"

// Three complete PAC periods in one EasyDMA transfer. The legacy streaming
// driver restarts each 2048-sample buffer in its IRQ; BLE can delay that restart
// and remove part of an NRZ interval. No CPU service is needed within this span.
#define PAC_FRAME_SAMPLES (128 * 32)
#define PAC_CAPTURE_SAMPLES (3 * PAC_FRAME_SAMPLES)
static nrf_saadc_value_t samples[PAC_CAPTURE_SAMPLES];
static volatile bool samples_ready;

static int16_t coherent_sample(unsigned position) {
    // PAC repeats at exactly 128 * RF/32 carrier cycles. Reject an excursion
    // confined to one physical period before slicing NRZ symbols. Keep the
    // original snapshot intact for diagnosis; feed the cyclic filtered period.
    unsigned n = position % PAC_FRAME_SAMPLES;
    int16_t a = samples[n], b = samples[n + PAC_FRAME_SAMPLES], c = samples[n + 2 * PAC_FRAME_SAMPLES];
    if (a > b) { int16_t t = a; a = b; b = t; }
    if (b > c) b = c;
    return a > b ? a : b;
}

static void pac_saadc_cb(nrf_saadc_value_t *buffer, size_t count) {
    samples_ready = buffer == samples && count == PAC_CAPTURE_SAMPLES;
}

size_t pac_copy_last_capture(uint8_t *data, size_t offset, size_t length) {
    if (!samples_ready || offset >= sizeof(samples)) return 0;
    if (length > sizeof(samples) - offset) length = sizeof(samples) - offset;
    memcpy(data, (const uint8_t *)samples + offset, length);
    return length;
}

bool pac_read(uint8_t *data, uint32_t timeout_ms) {
    const uint8_t widths[] = {75, PAC_DECODE_WIDE_HYSTERESIS, 95,
        PAC_DECODE_UNCLIPPED | 75, PAC_DECODE_UNCLIPPED | PAC_DECODE_WIDE_HYSTERESIS};
    void *codecs[sizeof(widths)] = {0};
    for (unsigned i = 0; i < sizeof(widths); i++) {
        codecs[i] = pac.alloc();
        if (!codecs[i]) {
            for (unsigned j = 0; j < i; j++) pac.free(codecs[j]);
            return false;
        }
    }

    stop_lf_125khz_radio();
    lf_125khz_radio_saadc_disable();
    bsp_delay_ms(50);
    start_lf_125khz_radio();
    bsp_delay_ms(10);

    bool ok = false;
    samples_ready = false;
    autotimer *timer = bsp_obtain_timer(0);
    // Do not replace the last complete snapshot with a partial capture when
    // less than one 98.304 ms transfer remains before the deadline.
    while (!ok && timeout_ms > 105 && NO_TIMEOUT_1MS(timer, timeout_ms - 105)) {
        for (unsigned i = 0; i < sizeof(widths); i++) pac.decoder.start(codecs[i], widths[i]);
        samples_ready = false;
        lf_125khz_radio_saadc_capture_enable(pac_saadc_cb, samples, PAC_CAPTURE_SAMPLES);
        while (!samples_ready && NO_TIMEOUT_1MS(timer, timeout_ms)) {}
        // Stop EasyDMA before decoding: this is a stable snapshot, with no
        // queue or rearmed buffer that can overwrite samples under the reader.
        lf_125khz_radio_saadc_disable();
        if (!samples_ready) break;
        for (unsigned n = 0; n < PAC_CAPTURE_SAMPLES && !ok && NO_TIMEOUT_1MS(timer, timeout_ms); n++) {
            uint16_t sample = (uint16_t)coherent_sample(n);
            for (unsigned i = 0; i < sizeof(widths); i++) {
                if (pac.decoder.feed(codecs[i], sample)) {
                    memcpy(data, pac.get_data(codecs[i]), pac.data_size);
                    ok = true;
                    break;
                }
            }
        }
    }
    bsp_return_timer(timer);
    stop_lf_125khz_radio();
    for (unsigned i = 0; i < sizeof(widths); i++) pac.free(codecs[i]);
    return ok;
}
