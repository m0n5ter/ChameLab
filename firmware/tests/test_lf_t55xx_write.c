// Decode the actual generated downlink bits into a strict T5577 model.
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "protocols/t55xx.h"
#include "timeslot.h"

static uint32_t memory[8];
static bool password_mode, latch_config_on_reset;
static uint8_t bits[80];
static unsigned bit_count, accepted_writes, last_data_block;

static uint32_t word_at(unsigned offset) {
    uint32_t word = 0;
    for (unsigned i = 0; i < 32; i++) word = (word << 1) | bits[offset + i];
    return word;
}

void request_timeslot(uint32_t duration, timeslot_callback_t callback) {
    (void)duration;
    bit_count = 0;
    callback();
    assert(bit_count >= 2);
    unsigned opcode = (bits[0] << 1) | bits[1];
    if (opcode == T5577_OPCODE_RESET) {
        if (latch_config_on_reset) password_mode = (memory[0] & T5577_PWD) != 0;
        return;
    }
    assert(opcode == T5577_OPCODE_PAGE0);
    assert(bit_count == 38 || bit_count == 70);
    bool has_password = bit_count == 70;
    unsigned lock = has_password ? 34 : 2;
    assert(bits[lock] == 0); // No permanent block locks may be set.
    if (password_mode != has_password) return;
    if (has_password && word_at(2) != memory[7]) return;
    unsigned address = (bits[bit_count - 3] << 2) | (bits[bit_count - 2] << 1) | bits[bit_count - 1];
    memory[address] = word_at(lock + 1);
    accepted_writes++;
    if (address != 7) last_data_block = address;
    if (address == 0 && !latch_config_on_reset) password_mode = (memory[0] & T5577_PWD) != 0;
}

void bsp_delay_us(uint32_t delay) {
    if (delay == 24 * 8 || delay == 54 * 8) {
        assert(bit_count < sizeof(bits));
        bits[bit_count++] = delay == 54 * 8;
    }
}
void bsp_delay_ms(uint16_t delay) { (void)delay; }
void start_lf_125khz_radio(void) {}
void stop_lf_125khz_radio(void) {}

static void run_case(bool protected, bool latch, unsigned count) {
    const uint32_t target[5] = {0x00148090, 0xFF8CA64A, 0x98F8C802, 0xFF84A64A, 0x98F8C802};
    uint32_t blocks[5];
    memcpy(blocks, target, sizeof(blocks));
    if (count == 3) blocks[0] = 0x00148050;
    memset(memory, 0, sizeof(memory));
    memory[0] = 0x00148040 | (protected ? T5577_PWD : 0);
    memory[7] = 0x12345678;
    password_mode = protected;
    latch_config_on_reset = latch;
    accepted_writes = last_data_block = 0;
    const uint32_t password = 0x20206666;
    if (protected) t55xx_reset_passwd(memory[7], password);
    t55xx_write_data(password, blocks, count);
    assert(memcmp(memory, blocks, count * sizeof(uint32_t)) == 0);
    assert(memory[7] == password);
    assert(password_mode);
    assert(last_data_block == 0);
    assert(accepted_writes >= count + 1);

    // The stored password must permit the next rewrite as well.
    t55xx_reset_passwd(password, 0x87654321);
    blocks[1] ^= 0x20;
    t55xx_write_data(0x87654321, blocks, count);
    assert(memcmp(memory, blocks, count * sizeof(uint32_t)) == 0);
    assert(memory[7] == 0x87654321);
}

int main(void) {
    for (unsigned protected = 0; protected < 2; protected++)
        for (unsigned latch = 0; latch < 2; latch++) {
            run_case(protected, latch, 3);
            run_case(protected, latch, 5);
        }
    puts("PASS: T5577 blank/protected, immediate/latched configuration, 64/128-bit frames, repeated rewrites, no permanent locks");
}
