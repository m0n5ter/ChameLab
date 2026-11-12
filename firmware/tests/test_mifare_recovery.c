#include <assert.h>
#include <string.h>
#include "mf1_toolbox.h"
#include "mf1_crapto1.h"
#include "parity.h"

enum { STATUS_HF_TAG_OK, STATUS_HF_TAG_NO, STATUS_MF_ERR_AUTH, STATUS_HF_ERR_STAT, STATUS_HF_ERR_CRC, STATUS_PAR_ERR };
enum { PICC_AUTHENT1A = 0x60, PICC_AUTHENT1B = 0x61 };
static int tag;
static int *p_tag_info = &tag;
static const uint32_t uid = 0xA13E4902, base_nonce = 0xD14191B3;
static const uint64_t known_key = 0xFFFFFFFFFFFFULL, target_key = 0x112233445566ULL;
static unsigned sends, auth_calls, start_sector, distance = 117;
static uint32_t pending_nonce;
static uint16_t auth_status;
static uint8_t trailer[16];
static bool check_mode, backdoor_mode;

static uint64_t bytes_to_num(const uint8_t *bytes, unsigned count) {
    uint64_t value = 0;
    while (count--) value = (value << 8) | *bytes++;
    return value;
}
static void num_to_bytes(uint64_t value, unsigned count, uint8_t *bytes) {
    while (count) { bytes[--count] = value; value >>= 8; }
}
static uint8_t pcd_14a_reader_scan_auto(int *info) { return STATUS_HF_TAG_OK; }
static uint8_t pcd_14a_reader_fast_select(int *info) { return STATUS_HF_TAG_OK; }
static uint32_t get_u32_tag_uid(int *info) { return uid; }
static void mf1_toolbox_report_healthy(void) { }
static void mf1_toolbox_antenna_restart(void) { }
uint16_t auth_key_use_522_hw(uint8_t block, uint8_t type, uint8_t *key) {
    auth_calls++;
    return auth_status;
}
static uint16_t pcd_14a_reader_mf1_read(uint8_t block, uint8_t *out) {
    memcpy(out, trailer, 16);
    return STATUS_HF_TAG_OK;
}
static int authex(struct Crypto1State *state, uint32_t card_uid, uint8_t block, uint8_t type,
    uint64_t key, uint8_t nested, uint32_t *nt) {
    auth_calls++;
    if (check_mode) return auth_status;
    if (backdoor_mode) {
        unsigned sector = start_sector + sends / 4;
        unsigned expected_block = sector < 32 ? sector * 4 : 128 + (sector - 32) * 16;
        assert(card_uid == uid && block == expected_block && type == 0x64 + (sends / 2) % 2);
        assert(key == known_key && nested == AUTH_FIRST);
        *nt = base_nonce;
        return auth_status;
    }
    assert(card_uid == uid && block == 19 && type == PICC_AUTHENT1A && key == known_key && nested == AUTH_FIRST);
    if (auth_status != STATUS_HF_TAG_OK) return auth_status;
    *nt = sends == 0 ? base_nonce : prng_successor(pending_nonce, distance);
    return STATUS_HF_TAG_OK;
}
static uint8_t send_cmd(struct Crypto1State *state, uint8_t nested, uint8_t type, uint8_t block,
    uint8_t *status, uint8_t *out, uint8_t *parity, unsigned bits) {
    assert(nested == AUTH_NESTED);
    if (backdoor_mode) {
        unsigned sector = start_sector + sends / 4;
        unsigned expected_block = sector < 32 ? sector * 4 : 128 + (sector - 32) * 16;
        assert(block == expected_block && type == ((sends & 1) ? 0x60 : 0x64) + (sends / 2) % 2);
        pending_nonce = prng_successor(base_nonce, (sends / 2) * 13);
        struct Crypto1State cipher;
        crypto1_init(&cipher, (sends & 1) ? target_key : known_key);
        num_to_bytes(pending_nonce ^ crypto1_word(&cipher, uid ^ pending_nonce, 0), 4, out);
        memset(parity, 0, 4);
        sends++;
        *status = STATUS_HF_TAG_OK;
        return 32;
    }
    if (sends == 0) assert(block == 19 && type == PICC_AUTHENT1A);
    else {
        unsigned sector = start_sector + (sends - 1) / 2;
        unsigned expected_block = sector < 32 ? sector * 4 : 128 + (sector - 32) * 16;
        assert(block == expected_block && type == PICC_AUTHENT1A + ((sends - 1) % 2));
    }
    pending_nonce = prng_successor(base_nonce, sends * 13);
    struct Crypto1State cipher;
    crypto1_init(&cipher, sends == 0 ? known_key : target_key);
    uint32_t enc = pending_nonce ^ crypto1_word(&cipher, uid ^ pending_nonce, 0);
    num_to_bytes(enc, 4, out);
    for (unsigned i = 0; i < 4; i++) parity[i] = oddparity8(enc >> (24 - 8 * i)) ^ ((sends + i) & 1);
    sends++;
    *status = STATUS_HF_TAG_OK;
    return 32;
}

// The script extracts these functions verbatim from the production source.
#include "recovery_under_test.c"

typedef struct {
    uint16_t command, status, length;
    uint8_t data[4 + 40 * sizeof(mf1_static_nonce_sector_t)];
} data_frame_tx_t;
static data_frame_tx_t response_frame;
static data_frame_tx_t *data_frame_make(uint16_t cmd, uint16_t status, uint16_t length, const uint8_t *data) {
    assert(length <= sizeof(response_frame.data));
    memset(&response_frame, 0, sizeof(response_frame));
    response_frame.command = cmd;
    response_frame.status = status;
    response_frame.length = length;
    if (length) memcpy(response_frame.data, data, length);
    return &response_frame;
}

// Include the production command handlers as well: collection alone does not
// exercise the response slicing that used to replace sector 32 with sector 0.
#include "sen_commands_under_test.c"

static void collect(unsigned first, unsigned count) {
    start_sector = first;
    sends = auth_calls = 0;
    check_mode = false;
    auth_status = STATUS_HF_TAG_OK;
    uint8_t captured[40][sizeof(mf1_static_nonce_sector_t)] = {{0}}, acquired = 0;
    uint32_t captured_uid = 0;
    assert(mf1_static_encrypted_nonces_with_key(known_key, 19, PICC_AUTHENT1A,
        count, first, captured, &acquired, &captured_uid) == STATUS_HF_TAG_OK);
    assert(captured_uid == uid && acquired == count - first);
    assert(sends == 1U + 2U * acquired);
    for (unsigned sector = first; sector < count; sector++)
        for (unsigned type = 0; type < 2; type++) {
            unsigned index = 1 + (sector - first) * 2 + type;
            uint32_t nt = prng_successor(base_nonce, index * 13);
            const uint8_t *record = captured[sector] + type * 7;
            assert(bytes_to_num(record, 2) == (nt >> 16));
            unsigned expected_parity = 0;
            for (unsigned i = 0; i < 4; i++) expected_parity = (expected_parity << 1) | ((index + i) & 1);
            assert(record[2] == expected_parity);
            struct Crypto1State cipher;
            crypto1_init(&cipher, target_key);
            assert(bytes_to_num(record + 3, 4) == (nt ^ crypto1_word(&cipher, uid ^ nt, 0)));
        }
}

static void batch_checks(void) {
    mf1_key_t key = {{1, 2, 3, 4, 5, 6}};
    mf1_toolbox_check_keys_of_sectors_in_t in = {.keys_len = 1, .keys = &key};
    memset(in.mask.b, 255, sizeof(in.mask.b));
    in.mask.b[0] &= ~0x80; // Only sector 0 key A.
    mf1_toolbox_check_keys_of_sectors_out_t out;
    memset(trailer, 0, sizeof(trailer));
    trailer[6] = 0xFF; trailer[7] = 0x07; trailer[8] = 0x80;
    auth_status = STATUS_HF_TAG_OK;
    assert(mf1_toolbox_check_keys_of_sectors(&in, &out) == STATUS_HF_TAG_OK);
    assert((out.found.b[0] & 0xC0) == 0xC0); // Readable zero B is retained.
    assert(bytes_to_num(out.keys[0][1].key, 6) == 0);
    trailer[7] ^= 0x80; // Broken access-bit redundancy must not disclose B.
    assert(mf1_toolbox_check_keys_of_sectors(&in, &out) == STATUS_HF_TAG_OK);
    assert((out.found.b[0] & 0xC0) == 0x80);
    auth_status = STATUS_HF_ERR_CRC;
    assert(mf1_toolbox_check_keys_of_sectors(&in, &out) == STATUS_HF_ERR_CRC);
    assert(out.found.b[0] == 0);

    mf1_toolbox_check_keys_on_block_in_t block = {.block = 3, .key_type = PICC_AUTHENT1B, .keys_len = 1, .keys = &key};
    mf1_toolbox_check_keys_on_block_out_t hit;
    check_mode = true;
    assert(mf1_toolbox_check_keys_on_block(&block, &hit) == STATUS_HF_ERR_CRC);
    assert(hit.found == 0);
}

static void collect_backdoor(unsigned first, unsigned count) {
    start_sector = first;
    sends = auth_calls = 0;
    backdoor_mode = true;
    check_mode = false;
    auth_status = STATUS_HF_TAG_OK;
    uint8_t captured[40][sizeof(mf1_static_nonce_sector_t)] = {{0}}, acquired = 0;
    uint32_t captured_uid = 0;
    assert(mf1_static_encrypted_nonces_acquire(known_key, count, first, captured,
        &acquired, &captured_uid) == STATUS_HF_TAG_OK);
    assert(acquired == count - first && captured_uid == uid && sends == 4U * acquired);
    for (unsigned sector = first; sector < count; sector++)
        for (unsigned type = 0; type < 2; type++) {
            unsigned index = (sector - first) * 2 + type;
            uint32_t nt = prng_successor(base_nonce, index * 13);
            const uint8_t *record = captured[sector] + type * 7;
            assert(bytes_to_num(record, 2) == (nt >> 16));
            struct Crypto1State cipher;
            crypto1_init(&cipher, target_key);
            assert(bytes_to_num(record + 3, 4) == (nt ^ crypto1_word(&cipher, uid ^ nt, 0)));
        }
    backdoor_mode = false;
}

static void check_range_response(const data_frame_tx_t *response, unsigned first, unsigned count, unsigned nonce_offset) {
    assert(response->status == STATUS_HF_TAG_OK);
    assert(response->length == 4 + (count - first) * sizeof(mf1_static_nonce_sector_t));
    assert(bytes_to_num(response->data, 4) == uid);
    for (unsigned sector = first; sector < count; sector++)
        for (unsigned type = 0; type < 2; type++) {
            unsigned index = nonce_offset + (sector - first) * 2 + type;
            uint32_t nt = prng_successor(base_nonce, index * 13);
            const uint8_t *record = response->data + 4 + (sector - first) * sizeof(mf1_static_nonce_sector_t) + type * 7;
            assert(bytes_to_num(record, 2) == (nt >> 16));
            struct Crypto1State cipher;
            crypto1_init(&cipher, target_key);
            assert(bytes_to_num(record + 3, 4) == (nt ^ crypto1_word(&cipher, uid ^ nt, 0)));
        }
}

static void command_range(unsigned first, unsigned count) {
    uint8_t payload[10] = {0};
    num_to_bytes(known_key, 6, payload);
    payload[6] = count;
    payload[7] = first;
    payload[8] = 19;
    payload[9] = PICC_AUTHENT1A;
    start_sector = first;
    sends = auth_calls = 0;
    check_mode = false;
    backdoor_mode = true;
    auth_status = STATUS_HF_TAG_OK;
    const data_frame_tx_t *response = cmd_processor_mf1_enc_nested_acquire(2014, 0, 8, payload);
    assert(response->command == 2014 && sends == 4 * (count - first));
    check_range_response(response, first, count, 0);
    sends = auth_calls = 0;
    backdoor_mode = false;
    response = cmd_processor_mf1_enc_nested_with_key(2023, 0, 10, payload);
    assert(response->command == 2023 && sends == 1 + 2 * (count - first));
    check_range_response(response, first, count, 1);
}

static void invalid_ranges(void) {
    uint8_t payload[8] = {0};
    const unsigned ranges[][2] = {{0,0}, {0,41}, {32,32}, {33,32}, {255,40}};
    sends = auth_calls = 0;
    for (unsigned i = 0; i < sizeof(ranges) / sizeof(ranges[0]); i++) {
        payload[7] = ranges[i][0];
        payload[6] = ranges[i][1];
        const data_frame_tx_t *response = cmd_processor_mf1_enc_nested_acquire(2014, 0, 8, payload);
        assert(response->status == STATUS_PAR_ERR && response->length == 0);
    }
    assert(cmd_processor_mf1_enc_nested_acquire(2014, 0, 7, payload)->status == STATUS_PAR_ERR);
    assert(sends == 0 && auth_calls == 0);
}

int main(void) {
    collect(0, 5);
    collect(0, 40);
    collect(32, 40);
    auth_status = STATUS_HF_ERR_CRC;
    uint8_t captured[40][sizeof(mf1_static_nonce_sector_t)], acquired;
    uint32_t captured_uid;
    assert(mf1_static_encrypted_nonces_with_key(known_key, 19, PICC_AUTHENT1A,
        5, 0, captured, &acquired, &captured_uid) == STATUS_HF_ERR_CRC);
    assert(acquired == 0);
    collect_backdoor(0, 16);
    collect_backdoor(32, 33); // FM11RF08S hidden sector must use block 128, not 143.
    collect_backdoor(32, 40); // Preserve actual 4K sector geometry as well.
    command_range(0, 16);
    command_range(1, 3);
    command_range(32, 33); // Actual wire reply for the RF08S hidden sector.
    command_range(32, 40);
    invalid_ranges();
    batch_checks();
    puts("MIFARE recovery: nonce rewind, 4K geometry, sector-range wire replies, invalid ranges, zero B and RF errors passed.");
    return 0;
}
