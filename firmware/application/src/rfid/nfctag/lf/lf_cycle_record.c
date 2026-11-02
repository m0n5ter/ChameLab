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

bool lf_cycle_prepare_save(uint8_t *data, uint16_t length) {
    if (!lf_cycle_valid(data, length)) return false;
    if (data[LF_CYCLE_VERSION] == 4) {
        // Keep every card and its order; only the obsolete bursts/silence and
        // padding bytes change. Doing this at save time lets the existing CRC
        // check detect the migration and persist the complete v5 record.
        data[LF_CYCLE_VERSION] = 5;
        memset(data + LF_CYCLE_COUNT + 1, 0, LF_CYCLE_EXTRAS - LF_CYCLE_COUNT - 1);
    }
    return true;
}
