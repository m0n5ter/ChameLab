#include "ble_radio_policy.h"

uint8_t ble_radio_policy_update(ble_radio_policy_t *policy, ble_radio_event_t event) {
    switch (event) {
        case BLE_RADIO_REQUEST_START: policy->requested = true; break;
        case BLE_RADIO_REQUEST_STOP: policy->requested = false; break;
        case BLE_RADIO_USB_OPEN: policy->usb_open = true; break;
        case BLE_RADIO_USB_CLOSE: policy->usb_open = false; break;
        case BLE_RADIO_CONNECTED:
            policy->connected = true;
            policy->advertising = false; // The SoftDevice stopped it on connection.
            policy->disconnect_pending = false;
            break;
        case BLE_RADIO_DISCONNECTED:
            policy->connected = false;
            policy->disconnect_pending = false;
            break;
    }

    uint8_t actions = 0;
    bool advertise = policy->requested && !policy->usb_open && !policy->connected;
    if (!advertise && policy->advertising) {
        policy->advertising = false;
        actions |= BLE_RADIO_STOP_ADV;
    }
    if (policy->usb_open && policy->connected && !policy->disconnect_pending) {
        policy->disconnect_pending = true;
        actions |= BLE_RADIO_DISCONNECT;
    }
    if (advertise && !policy->advertising) {
        policy->advertising = true;
        actions |= BLE_RADIO_START_ADV;
    }
    return actions;
}
