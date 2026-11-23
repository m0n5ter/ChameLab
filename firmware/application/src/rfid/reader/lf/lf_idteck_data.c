#include <string.h>

#include "bsp_delay.h"
#include "bsp_time.h"
#include "circular_buffer.h"
#include "lf_125khz_radio.h"
#include "lf_reader_data.h"
#include "protocols/idteck.h"

static circular_buffer cb;
static volatile bool active;

static void idteck_saadc_cb(nrf_saadc_value_t *samples, size_t count) {
    if (!active) return;
    for (size_t i = 0; i < count && active; i++)
        if (!cb_push_back(&cb, &samples[i])) break;
}

bool idteck_read(uint8_t *data, uint32_t timeout_ms) {
    void *codec = idteck.alloc();
    if (!codec) return false;
    if (!cb_init(&cb, 6144, sizeof(int16_t))) {
        idteck.free(codec);
        return false;
    }
    idteck.decoder.start(codec, 0);
    stop_lf_125khz_radio();
    lf_125khz_radio_saadc_disable();
    bsp_delay_ms(50);
    start_lf_125khz_radio();
    bsp_delay_ms(10);
    active = true;
    lf_125khz_radio_saadc_enable(idteck_saadc_cb);

    bool ok = false;
    autotimer *timer = bsp_obtain_timer(0);
    while (!ok && NO_TIMEOUT_1MS(timer, timeout_ms)) {
        uint16_t sample;
        while (!ok && NO_TIMEOUT_1MS(timer, timeout_ms) && cb_pop_front(&cb, &sample))
            ok = idteck.decoder.feed(codec, sample);
    }
    bsp_return_timer(timer);
    active = false;
    lf_125khz_radio_saadc_disable();
    stop_lf_125khz_radio();
    if (ok) memcpy(data, idteck.get_data(codec), IDTECK_DATA_SIZE);
    cb_free(&cb);
    idteck.free(codec);
    return ok;
}
