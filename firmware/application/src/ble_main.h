#ifndef BLE_MAIN_H
#define BLE_MAIN_H

#include "ble_bas.h"
#include "ble_gatts.h"
#include "ble_nus.h"
#include "nrfx_saadc.h"

extern uint16_t batt_lvl_in_milli_volts;
extern uint8_t percentage_batt_lvl;

typedef void (*lf_adc_callback_t)(nrf_saadc_value_t *, size_t);

void ble_slave_init(void);
void battery_level_measure_now(void);
void advertising_start(bool erase_bonds);
void advertising_stop(void);
void ble_usb_cdc_set_open(bool open);
void delete_bonds_all(void);
void nus_data_response(uint8_t *p_data, uint16_t length);
bool is_nus_working(void);
void set_ble_connect_key(uint8_t *key);

void register_lf_adc_callback(lf_adc_callback_t cb);
// One contiguous EasyDMA capture; the caller owns the buffer until disable.
void register_lf_adc_capture(lf_adc_callback_t cb, nrf_saadc_value_t *buffer, uint16_t count);
void unregister_lf_adc_callback(void);

#endif
