#include "lf_125khz_radio.h"

#include "lf_reader_data.h"
#include "bsp_time.h"
#include "bsp_delay.h"
#include "nrf_gpio.h"
#include "nrf_spim.h"
#include "nrfx_clock.h"
#include "nrfx_gpiote.h"
#include "nrfx_ppi.h"
#include "nrfx_pwm.h"
#include "nrfx_saadc.h"
#include "nrfx_timer.h"
#include "rfid_main.h"

static bool m_reader_inited = false;
nrfx_pwm_t m_pwm = NRFX_PWM_INSTANCE(0);
nrfx_timer_t m_pwm_timer_counter = NRFX_TIMER_INSTANCE(2);
nrf_ppi_channel_t m_pwm_saadc_sample_ppi_channel;
nrf_ppi_channel_t m_pwm_timer_count_ppi_channel;
static nrf_ppi_channel_t m_edge_capture_ppi_channel;
static bool m_edge_capture_enabled;
static uint32_t m_last_edge_count;

// At present, only channel 1 is used, so only one channel can be configured
static nrf_pwm_values_individual_t m_lf_125khz_pwm_seq_val[] = {
    {2, 0, 0, 0},
};

nrf_pwm_sequence_t const m_lf_125khz_pwm_seq_obj = {
    .values.p_individual = m_lf_125khz_pwm_seq_val,
    .length = NRF_PWM_VALUES_LENGTH(m_lf_125khz_pwm_seq_val),
    .repeats = 0,
    .end_delay = 0
};

/**
 * LF reading card decrease along the trigger collection event
 */
static void lf_125khz_gpio_handler(nrfx_gpiote_pin_t pin, nrf_gpiote_polarity_t action) {
    // Directly transfer to the event
    gpio_int0_irq_handler();
}

// The LF collection decline is interrupted, and the GPIO is pulled down
// by default. The trigger method is triggering
static void gpiote_init(bool high_accuracy) {
    nrfx_err_t err_code;

    nrfx_gpiote_in_config_t cfg = NRFX_GPIOTE_CONFIG_IN_SENSE_LOTOHI(high_accuracy);
    err_code = nrfx_gpiote_in_init(LF_OA_OUT, &cfg, lf_125khz_gpio_handler);
    APP_ERROR_CHECK(err_code);
}

/**
 * Start the 125kHz broadcast
 */
void start_lf_125khz_radio(void) {
    nrfx_pwm_simple_playback(&m_pwm, &m_lf_125khz_pwm_seq_obj, 1, NRFX_PWM_FLAG_LOOP);
    TAG_FIELD_LED_ON();
}

/**
 * Close 125kHz RF broadcast
 */
void stop_lf_125khz_radio(void) {
    nrfx_pwm_stop(&m_pwm, true);
    TAG_FIELD_LED_OFF();
}

static void pwm_init(void) {
    nrfx_pwm_config_t config = NRFX_PWM_DEFAULT_CONFIG;
    config.output_pins[0] = LF_ANT_DRIVER | NRFX_PWM_PIN_INVERTED;
    for (uint8_t i = 1; i < NRF_PWM_CHANNEL_COUNT; i++) {
        config.output_pins[i] = NRFX_PWM_PIN_NOT_USED;
    }
    config.irq_priority = APP_IRQ_PRIORITY_LOW;
    config.base_clock = (nrf_pwm_clk_t)NRF_PWM_CLK_500kHz;
    config.count_mode = (nrf_pwm_mode_t)NRF_PWM_MODE_UP;
    config.top_value = (uint16_t)4;
    config.load_mode = (nrf_pwm_dec_load_t)NRF_PWM_LOAD_INDIVIDUAL;
    config.step_mode = (nrf_pwm_dec_step_t)NRF_PWM_STEP_AUTO;

    nrfx_err_t err_code = nrfx_pwm_init(&m_pwm, &config, NULL);
    APP_ERROR_CHECK(err_code);
}

static void pwm_timer_counter_init(void) {
    nrfx_err_t err_code;

    nrfx_timer_config_t timer_cfg = NRFX_TIMER_DEFAULT_CONFIG;
    timer_cfg.mode = NRF_TIMER_MODE_COUNTER;
    timer_cfg.bit_width = NRF_TIMER_BIT_WIDTH_32;

    err_code = nrfx_timer_init(&m_pwm_timer_counter, &timer_cfg, NULL);
    APP_ERROR_CHECK(err_code);
}

// trigger timer count task from pwm
static void pwm_timer_count_ppi_init(void) {
    nrfx_err_t err_code;

    err_code = nrfx_ppi_channel_alloc(&m_pwm_timer_count_ppi_channel);
    APP_ERROR_CHECK(err_code);

    err_code = nrfx_ppi_channel_assign(
                   m_pwm_timer_count_ppi_channel,
                   nrfx_pwm_event_address_get(&m_pwm, NRF_PWM_EVENT_PWMPERIODEND),
                   nrfx_timer_task_address_get(&m_pwm_timer_counter, NRF_TIMER_TASK_COUNT));
    APP_ERROR_CHECK(err_code);
}

// trigger saadc sample task from pwm
static void pwm_saadc_sample_ppi_init(void) {
    nrfx_err_t err_code;

    err_code = nrfx_ppi_channel_alloc(&m_pwm_saadc_sample_ppi_channel);
    APP_ERROR_CHECK(err_code);

    err_code = nrfx_ppi_channel_assign(
                   m_pwm_saadc_sample_ppi_channel,
                   nrfx_pwm_event_address_get(&m_pwm, NRF_PWM_EVENT_PWMPERIODEND),
                   nrf_saadc_task_address_get(NRF_SAADC_TASK_SAMPLE));
    APP_ERROR_CHECK(err_code);
}

void lf_125khz_radio_saadc_enable(lf_adc_callback_t cb) {
    register_lf_adc_callback(cb);

    nrfx_err_t err_code;
    err_code = nrfx_ppi_channel_enable(m_pwm_saadc_sample_ppi_channel);
    APP_ERROR_CHECK(err_code);
}

void lf_125khz_radio_saadc_disable(void) {
    nrfx_err_t err_code;
    err_code = nrfx_ppi_channel_disable(m_pwm_saadc_sample_ppi_channel);
    APP_ERROR_CHECK(err_code);

    unregister_lf_adc_callback();
}

void lf_125khz_radio_gpiote_enable(void) {
    nrfx_err_t err_code;
    err_code = nrfx_ppi_channel_enable(m_pwm_timer_count_ppi_channel);
    APP_ERROR_CHECK(err_code);

    gpiote_init(false);
    nrfx_timer_enable(&m_pwm_timer_counter);
    nrfx_gpiote_in_event_enable(LF_OA_OUT, true);
}

void lf_125khz_radio_gpiote_capture_enable(void) {
    // A dedicated IN event latches the carrier count at the edge, even while
    // the SoftDevice is delaying the GPIO ISR. PORT/SENSE cannot do this.
    gpiote_init(true);
    nrfx_err_t err_code = nrfx_ppi_channel_alloc(&m_edge_capture_ppi_channel);
    APP_ERROR_CHECK(err_code);
    err_code = nrfx_ppi_channel_assign(
                   m_edge_capture_ppi_channel,
                   nrfx_gpiote_in_event_addr_get(LF_OA_OUT),
                   nrfx_timer_task_address_get(&m_pwm_timer_counter, NRF_TIMER_TASK_CAPTURE0));
    APP_ERROR_CHECK(err_code);
    nrfx_timer_clear(&m_pwm_timer_counter);
    m_last_edge_count = 0;
    m_edge_capture_enabled = true;
    err_code = nrfx_ppi_channel_enable(m_edge_capture_ppi_channel);
    APP_ERROR_CHECK(err_code);
    err_code = nrfx_ppi_channel_enable(m_pwm_timer_count_ppi_channel);
    APP_ERROR_CHECK(err_code);
    nrfx_timer_enable(&m_pwm_timer_counter);
    nrfx_gpiote_in_event_enable(LF_OA_OUT, true);
}

uint32_t lf_125khz_radio_edge_interval(void) {
    uint32_t count = nrfx_timer_capture_get(&m_pwm_timer_counter, NRF_TIMER_CC_CHANNEL0);
    // Unsigned subtraction handles counter wrap; never clear it in the ISR.
    uint32_t interval = count - m_last_edge_count;
    m_last_edge_count = count;
    return interval;
}

bool lf_125khz_radio_capture_bits(uint8_t *buffer, size_t size, uint32_t timeout_ms) {
    // SPIM2 is unused by the reader (RC522 uses SPI0). Sample the comparator
    // into EasyDMA at one bit per carrier cycle. MOSI is disconnected;
    // the field indicator LED temporarily carries SCK (no RF signal is driven).
    // Unlike GPIOTE, DMA retains every sample across long BLE interrupts.
    NRF_SPIM_Type *spim = NRF_SPIM2;
    nrf_spim_disable(spim);
    nrf_spim_pins_set(spim, LED_FIELD, NRF_SPIM_PIN_NOT_CONNECTED, LF_OA_OUT);
    nrf_gpio_cfg_input(LF_OA_OUT, NRF_GPIO_PIN_NOPULL);
    nrf_spim_configure(spim, NRF_SPIM_MODE_0, NRF_SPIM_BIT_ORDER_MSB_FIRST);
    nrf_spim_frequency_set(spim, NRF_SPIM_FREQ_125K);
    nrf_spim_rx_buffer_set(spim, buffer, size);
    nrf_spim_tx_buffer_set(spim, buffer, size);
    nrf_spim_event_clear(spim, NRF_SPIM_EVENT_END);
    nrf_spim_event_clear(spim, NRF_SPIM_EVENT_STOPPED);
    nrf_spim_enable(spim);
    autotimer *timer = bsp_obtain_timer(0);
    nrf_spim_task_trigger(spim, NRF_SPIM_TASK_START);
    while (!nrf_spim_event_check(spim, NRF_SPIM_EVENT_END) && NO_TIMEOUT_1MS(timer, timeout_ms)) {}
    bool complete = nrf_spim_event_check(spim, NRF_SPIM_EVENT_END);
    if (!complete) {
        nrf_spim_task_trigger(spim, NRF_SPIM_TASK_STOP);
        for (unsigned wait = 0; wait < 1000 && !nrf_spim_event_check(spim, NRF_SPIM_EVENT_STOPPED); wait++)
            bsp_delay_us(1);
    }
    bsp_return_timer(timer);
    nrf_spim_disable(spim);
    nrf_spim_pins_set(spim, NRF_SPIM_PIN_NOT_CONNECTED, NRF_SPIM_PIN_NOT_CONNECTED, NRF_SPIM_PIN_NOT_CONNECTED);
    TAG_FIELD_LED_ON();
    return complete;
}

void lf_125khz_radio_gpiote_disable(void) {
    nrfx_gpiote_in_event_disable(LF_OA_OUT);
    if (m_edge_capture_enabled) {
        nrfx_err_t err_code = nrfx_ppi_channel_disable(m_edge_capture_ppi_channel);
        APP_ERROR_CHECK(err_code);
        err_code = nrfx_ppi_channel_free(m_edge_capture_ppi_channel);
        APP_ERROR_CHECK(err_code);
        m_edge_capture_enabled = false;
    }
    nrfx_gpiote_in_uninit(LF_OA_OUT);
    nrfx_timer_disable(&m_pwm_timer_counter);

    nrfx_err_t err_code;
    err_code = nrfx_ppi_channel_disable(m_pwm_timer_count_ppi_channel);
    APP_ERROR_CHECK(err_code);
}

// init 125kHz signal PWM modulation (use gpiote for ASK & saadc for FSK)
void lf_125khz_radio_init(void) {
    if (!m_reader_inited) {
        pwm_init();
        pwm_timer_counter_init();
        pwm_timer_count_ppi_init();
        pwm_saadc_sample_ppi_init();
        m_reader_inited = true;
    }
}

// uninitialize
void lf_125khz_radio_uninit(void) {
    if (m_reader_inited) {
        nrfx_ppi_channel_free(m_pwm_saadc_sample_ppi_channel);
        nrfx_ppi_channel_free(m_pwm_timer_count_ppi_channel);
        nrfx_timer_uninit(&m_pwm_timer_counter);
        nrfx_pwm_uninit(&m_pwm);
        m_reader_inited = false;
    }
}
