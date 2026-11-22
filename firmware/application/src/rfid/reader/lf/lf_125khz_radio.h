#pragma once

#include "ble_main.h"
#include "nrfx_pwm.h"

/* Exposed so lf_gap.c can stop the PWM and drive LF_ANT_DRIVER directly
 * to create clean field gaps without relying on PWM pin release state. */
extern nrfx_pwm_t m_pwm;

void lf_125khz_radio_init(void);
void lf_125khz_radio_uninit(void);

void lf_125khz_radio_saadc_enable(lf_adc_callback_t cb);
void lf_125khz_radio_gpiote_enable(void);
// Hardware-timed edge intervals for EM410x/Electra. Shares the normal disable.
void lf_125khz_radio_gpiote_capture_enable(void);
uint32_t lf_125khz_radio_edge_interval(void);
bool lf_125khz_radio_capture_bits(uint8_t *buffer, size_t size, uint32_t timeout_ms);
void lf_125khz_radio_saadc_disable(void);
void lf_125khz_radio_gpiote_disable(void);

void start_lf_125khz_radio(void);
void stop_lf_125khz_radio(void);
