#pragma once

#include <stdbool.h>
#include <stdint.h>

typedef enum {
    BLE_RADIO_REQUEST_START,
    BLE_RADIO_REQUEST_STOP,
    BLE_RADIO_USB_OPEN,
    BLE_RADIO_USB_CLOSE,
    BLE_RADIO_CONNECTED,
    BLE_RADIO_DISCONNECTED,
} ble_radio_event_t;

enum {
    BLE_RADIO_START_ADV = 1,
    BLE_RADIO_STOP_ADV = 2,
    BLE_RADIO_DISCONNECT = 4,
};

typedef struct {
    bool requested;
    bool usb_open;
    bool connected;
    bool advertising;
    bool disconnect_pending;
} ble_radio_policy_t;

// USB owns the command transport while CDC/DTR is open. Retain the advertising
// request during that session so closing the port can restore discoverability.
uint8_t ble_radio_policy_update(ble_radio_policy_t *policy, ble_radio_event_t event);
