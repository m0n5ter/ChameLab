#include "lf_cycle_record.h"

#include <string.h>

bool lf_cycle_valid(const uint8_t *data, uint16_t length) {
    if (length < LF_CYCLE_SIZE || data[LF_CYCLE_MARKER] != 0xC7 ||
        (data[LF_CYCLE_VERSION] != 4 && data[LF_CYCLE_VERSION] != 5) ||
        data[LF_CYCLE_COUNT] < 2 || data[LF_CYCLE_COUNT] > LF_CYCLE_MAX)
        return false;
    for (uint8_t i = 1; i < data[LF_CYCLE_COUNT]; i++) {
        const uint8_t *entry = data + LF_CYCLE_EXTRAS + (i - 1) * LF_CYCLE_ENTRY_SIZE;
        if (lf_tag_id_size((tag_specific_type_t)((entry[0] << 8) | entry[1])) == 0) return false;
    }
    return true;
}

lf_cycle_settings_t lf_cycle_settings(const uint8_t *data, uint16_t length) {
    lf_cycle_settings_t s = {LF_CYCLE_MODE_PER_FIELD, 1, 0};
    if (!lf_cycle_valid(data, length)) return s;
    if (data[LF_CYCLE_VERSION] == 4) {
        // v4 only cycled while the field was present, bursts and silence in bytes 19/20.
        s.mode = LF_CYCLE_MODE_WHILE_PRESENT;
        s.bursts = data[19];
        s.silence = data[20];
    } else {
        s.mode = data[LF_CYCLE_MODE];
        s.bursts = data[LF_CYCLE_BURSTS];
        s.silence = data[LF_CYCLE_SILENCE];
    }
    if (s.mode > LF_CYCLE_MODE_WHILE_PRESENT) s.mode = LF_CYCLE_MODE_PER_FIELD;
    if (s.bursts < 1 || s.bursts > LF_CYCLE_BURSTS_MAX) s.bursts = 1;
    if (s.silence > LF_CYCLE_SILENCE_MAX) s.silence = 0;
    return s;
}

bool lf_cycle_settings_valid(lf_cycle_settings_t settings) {
    return settings.mode <= LF_CYCLE_MODE_WHILE_PRESENT && settings.bursts >= 1 &&
           settings.bursts <= LF_CYCLE_BURSTS_MAX && settings.silence <= LF_CYCLE_SILENCE_MAX;
}

void lf_cycle_store_settings(uint8_t *data, lf_cycle_settings_t settings) {
    data[LF_CYCLE_MODE] = settings.mode;
    data[LF_CYCLE_BURSTS] = settings.bursts;
    data[LF_CYCLE_SILENCE] = settings.silence;
}

bool lf_cycle_prepare_save(uint8_t *data, uint16_t length) {
    if (!lf_cycle_valid(data, length)) return false;
    if (data[LF_CYCLE_VERSION] == 4) {
        // Keep every card, its order and the while-present timing; only the layout
        // of the settings bytes changes. Doing this at save time lets the existing
        // CRC check detect the migration and persist the complete v5 record.
        lf_cycle_settings_t s = lf_cycle_settings(data, length);
        data[LF_CYCLE_VERSION] = 5;
        memset(data + LF_CYCLE_COUNT + 1, 0, LF_CYCLE_EXTRAS - LF_CYCLE_COUNT - 1);
        lf_cycle_store_settings(data, s);
    }
    return true;
}
