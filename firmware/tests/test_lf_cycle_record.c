#include <assert.h>
#include <stdio.h>
#include <string.h>

#include "lf_cycle_record.h"

// Stand in for the hardware protocol registry; the record code itself is the
// production implementation linked from lf_cycle_record.c.
uint16_t lf_tag_id_size(tag_specific_type_t type) {
    switch (type) {
        case TAG_TYPE_EM410X: return 5;
        case TAG_TYPE_HID_PROX: return 13;
        case TAG_TYPE_IDTECK: return 8;
        case TAG_TYPE_FARPOINTE_PYRAMID: return 16;
        default: return 0;
    }
}

static void make_record(uint8_t *record, uint8_t version, uint8_t count) {
    static const tag_specific_type_t types[] = {
        TAG_TYPE_EM410X, TAG_TYPE_HID_PROX, TAG_TYPE_IDTECK, TAG_TYPE_FARPOINTE_PYRAMID
    };
    memset(record, 0, LF_CYCLE_SIZE);
    memcpy(record, "\x01\x23\x45\x67\x89", 5);
    record[16] = 0xC7;
    record[17] = version;
    record[18] = count;
    record[19] = 10;  // old bursts
    record[20] = 30;  // old silence
    for (uint8_t i = 1; i < count; i++) {
        uint8_t *entry = record + 24 + (i - 1) * 18;
        tag_specific_type_t type = types[(i - 1) % 4];
        entry[0] = (uint8_t)(type >> 8);
        entry[1] = (uint8_t)type;
        for (uint8_t j = 0; j < 16; j++) entry[2 + j] = (uint8_t)(16 * i + j);
    }
}

static void test_v4_update_preserves_every_card(void) {
    for (uint8_t count = 2; count <= 8; count++) {
        uint8_t flash[LF_CYCLE_SIZE], loaded[LF_CYCLE_SIZE], saved[LF_CYCLE_SIZE];
        make_record(flash, 4, count);
        memcpy(loaded, flash, sizeof(loaded));

        // Both loading the active slot and querying inactive flash records must
        // recognize the old list without modifying it or losing its last card.
        assert(lf_cycle_valid(flash, sizeof(flash)));
        assert(lf_cycle_valid(loaded, sizeof(loaded)));
        assert(memcmp(flash, loaded, sizeof(flash)) == 0);

        assert(lf_cycle_prepare_save(loaded, sizeof(loaded)));
        assert(loaded[17] == 5);
        assert(loaded[18] == count);
        assert(memcmp(loaded, flash, 16) == 0);
        assert(memcmp(loaded + 24, flash + 24, sizeof(flash) - 24) == 0);
        for (unsigned i = 19; i < 24; i++) assert(loaded[i] == 0);

        // Save and reboot; a second migration must be an exact no-op.
        memcpy(saved, loaded, sizeof(saved));
        assert(lf_cycle_valid(saved, sizeof(saved)));
        assert(lf_cycle_prepare_save(saved, sizeof(saved)));
        assert(memcmp(saved, loaded, sizeof(saved)) == 0);
    }
}

static void test_v5_is_unchanged(void) {
    uint8_t record[LF_CYCLE_SIZE], before[LF_CYCLE_SIZE];
    make_record(record, 5, 8);
    memcpy(before, record, sizeof(before));
    assert(lf_cycle_valid(record, sizeof(record)));
    assert(lf_cycle_prepare_save(record, sizeof(record)));
    assert(memcmp(record, before, sizeof(before)) == 0);
}

static void test_obsolete_timings_are_ignored(void) {
    uint8_t record[LF_CYCLE_SIZE];
    make_record(record, 4, 2);
    memset(record + 19, 0xFF, 5);
    assert(lf_cycle_valid(record, sizeof(record)));
    assert(lf_cycle_prepare_save(record, sizeof(record)));
    for (unsigned i = 19; i < 24; i++) assert(record[i] == 0);
}

static void assert_rejected_without_changes(uint8_t *record, uint16_t length) {
    uint8_t before[LF_CYCLE_SIZE];
    memcpy(before, record, sizeof(before));
    assert(!lf_cycle_valid(record, length));
    assert(!lf_cycle_prepare_save(record, length));
    assert(memcmp(record, before, sizeof(before)) == 0);
}

static void test_invalid_records_are_rejected(void) {
    uint8_t record[LF_CYCLE_SIZE];
    for (uint8_t version = 4; version <= 5; version++) {
        make_record(record, version, 8);
        for (uint16_t length = 0; length < LF_CYCLE_SIZE; length++)
            assert_rejected_without_changes(record, length);
        record[16] = 0;
        assert_rejected_without_changes(record, sizeof(record));

        make_record(record, version, 8);
        for (unsigned count = 0; count <= 255; count++) {
            if (count >= 2 && count <= 8) continue;
            record[18] = (uint8_t)count;
            assert_rejected_without_changes(record, sizeof(record));
        }

        make_record(record, version, 8);
        // Reject a bad type in the very last card before changing the record.
        record[24 + 6 * 18] = 0xFF;
        record[24 + 6 * 18 + 1] = 0xFF;
        assert_rejected_without_changes(record, sizeof(record));
    }
    make_record(record, 5, 2);
    for (unsigned version = 0; version <= 255; version++) {
        if (version == 4 || version == 5) continue;
        record[17] = (uint8_t)version;
        assert_rejected_without_changes(record, sizeof(record));
    }
    assert(!lf_cycle_valid(NULL, 0));
}

int main(void) {
    test_v4_update_preserves_every_card();
    test_v5_is_unchanged();
    test_obsolete_timings_are_ignored();
    test_invalid_records_are_rejected();
    puts("LF cycle records: migration, save/reload, current records and invalid data passed");
    return 0;
}
