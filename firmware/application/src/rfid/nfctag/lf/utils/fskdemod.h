#pragma once

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define FSK_BITRATE_HID   (50)
#define FSK_BITRATE_IOPROX (64)
#define FSK_MAX_BITRATE   (64)    // RF/64 is the slowest FSK rate read
#define FSK_MAX_PHASES    (5)

typedef struct {
    uint8_t bitrate; // 50 or 64
    uint8_t c;
    bool dc_block;   // subtract a running DC baseline before Goertzel (off by default)
    bool dc_seen;    // running-baseline initialised
    float dc_run;    // IIR-tracked DC baseline
    uint16_t samples[FSK_MAX_BITRATE];  // with align: ring of the last bitrate samples
    float goertzel_fc_8;
    float goertzel_fc_10;

    // Bit timing (off by default). Without it a bit is decided over each
    // bitrate-sample window from wherever sampling started; a window that
    // starts mid-bit mixes both tones at every bit change and decides it by
    // a near coin toss. With align, a window ends every step samples
    // (phases per bit) and fsk_feed returns each one's bit with its phase in
    // out_phase; the decoder keeps a bit stream per phase and finds the frame
    // in the one that lines up with the tag's bits.
    bool align;
    uint8_t out_phase; // phase of the bit fsk_feed just returned
    uint8_t phases;    // windows per bit (set by fsk_alloc)
    uint8_t step;      // samples per block: a window is the last phases blocks
    uint8_t tick;      // sample index mod 40 (both tones repeat every 40 samples)
    uint8_t block;     // ring slot of the next block
    uint8_t blocks;    // complete blocks so far, up to phases
    float sum[4];      // this block's fc/8 and fc/10 correlations (re, im, re, im)
    float ring[FSK_MAX_PHASES][4];
} fsk_t;

extern bool fsk_feed(fsk_t *m, uint16_t sample, bool *bit);
extern fsk_t *fsk_alloc(uint8_t bitrate);
extern void fsk_free(fsk_t *m);

#ifdef __cplusplus
}
#endif
