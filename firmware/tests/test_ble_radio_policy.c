#include <assert.h>
#include <stdio.h>
#include "ble_radio_policy.h"

static void usb_session(void) {
    ble_radio_policy_t p = {0};
    assert(ble_radio_policy_update(&p, BLE_RADIO_REQUEST_START) == BLE_RADIO_START_ADV);
    assert(ble_radio_policy_update(&p, BLE_RADIO_USB_OPEN) == BLE_RADIO_STOP_ADV);
    // Wake-up and peer-manager callbacks cannot restart advertising over USB.
    assert(ble_radio_policy_update(&p, BLE_RADIO_REQUEST_START) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_USB_OPEN) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_USB_CLOSE) == BLE_RADIO_START_ADV);
    assert(ble_radio_policy_update(&p, BLE_RADIO_USB_CLOSE) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_REQUEST_STOP) == BLE_RADIO_STOP_ADV);
    // A closed/unplugged port must not advertise if it was never requested.
    assert(ble_radio_policy_update(&p, BLE_RADIO_USB_OPEN) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_USB_CLOSE) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_USB_OPEN) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_REQUEST_START) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_REQUEST_STOP) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_USB_CLOSE) == 0);
}

static void usb_takes_over_connection(void) {
    ble_radio_policy_t p = {0};
    assert(ble_radio_policy_update(&p, BLE_RADIO_REQUEST_START) == BLE_RADIO_START_ADV);
    assert(ble_radio_policy_update(&p, BLE_RADIO_CONNECTED) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_USB_OPEN) == BLE_RADIO_DISCONNECT);
    assert(ble_radio_policy_update(&p, BLE_RADIO_USB_OPEN) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_REQUEST_START) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_DISCONNECTED) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_USB_CLOSE) == BLE_RADIO_START_ADV);
}

static void disconnect_completes_after_usb_close(void) {
    ble_radio_policy_t p = {0};
    ble_radio_policy_update(&p, BLE_RADIO_REQUEST_START);
    ble_radio_policy_update(&p, BLE_RADIO_CONNECTED);
    assert(ble_radio_policy_update(&p, BLE_RADIO_USB_OPEN) == BLE_RADIO_DISCONNECT);
    assert(ble_radio_policy_update(&p, BLE_RADIO_USB_CLOSE) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_DISCONNECTED) == BLE_RADIO_START_ADV);
    assert(ble_radio_policy_update(&p, BLE_RADIO_DISCONNECTED) == 0);
}

static void connection_event_arrives_after_usb_open(void) {
    ble_radio_policy_t p = {0};
    ble_radio_policy_update(&p, BLE_RADIO_REQUEST_START);
    assert(ble_radio_policy_update(&p, BLE_RADIO_USB_OPEN) == BLE_RADIO_STOP_ADV);
    assert(ble_radio_policy_update(&p, BLE_RADIO_CONNECTED) == BLE_RADIO_DISCONNECT);
    assert(ble_radio_policy_update(&p, BLE_RADIO_DISCONNECTED) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_USB_CLOSE) == BLE_RADIO_START_ADV);
}

static void ble_without_usb(void) {
    ble_radio_policy_t p = {0};
    ble_radio_policy_update(&p, BLE_RADIO_REQUEST_START);
    assert(ble_radio_policy_update(&p, BLE_RADIO_CONNECTED) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_REQUEST_START) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_DISCONNECTED) == BLE_RADIO_START_ADV);
    ble_radio_policy_update(&p, BLE_RADIO_CONNECTED);
    assert(ble_radio_policy_update(&p, BLE_RADIO_REQUEST_STOP) == 0);
    assert(ble_radio_policy_update(&p, BLE_RADIO_DISCONNECTED) == 0);
}

static void check_all_sequences(ble_radio_policy_t p, unsigned depth) {
    if (depth == 0) return;
    for (unsigned e = BLE_RADIO_REQUEST_START; e <= BLE_RADIO_DISCONNECTED; e++) {
        ble_radio_policy_t next = p;
        uint8_t action = ble_radio_policy_update(&next, (ble_radio_event_t)e);
        assert(!next.advertising || (next.requested && !next.usb_open && !next.connected));
        assert(!next.disconnect_pending || next.connected);
        assert((action & (BLE_RADIO_START_ADV | BLE_RADIO_STOP_ADV)) !=
               (BLE_RADIO_START_ADV | BLE_RADIO_STOP_ADV));
        assert(!(action & BLE_RADIO_DISCONNECT) || (next.connected && next.usb_open));
        check_all_sequences(next, depth - 1);
    }
}

int main(void) {
    usb_session();
    usb_takes_over_connection();
    disconnect_completes_after_usb_close();
    connection_event_arrives_after_usb_open();
    ble_without_usb();
    check_all_sequences((ble_radio_policy_t){0}, 7);
    puts("PASS: USB/BLE ownership, restoration, late connection events, idempotence, all event sequences through depth 7");
}
