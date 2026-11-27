#include "mf1_crypto1.h"
/* 32-bit state and streaming implementation adapted from unkernet/ChameleonUltra,
 * commit 2f589d95c0b5b8a4d9b4b502766f1b0f9a0eb9bd. */
#include "parity.h"
#include "crc_utils.h"
#include <stddef.h>
#include <cmsis_gcc.h>

#define __inline__ inline
#define ODD_PARITY oddparity8

#define PRNG_MASK        0x002D0000UL
/* x^16 + x^14 + x^13 + x^11 + 1 */

#define PRNG_SIZE        4 /* Bytes */
#define NONCE_SIZE       4 /* Bytes */

#define LFSR_MASK_EVEN   0x2010E1UL
#define LFSR_MASK_ODD    0x3A7394UL
/* x^48 + x^43 + x^39 + x^38 + x^36 + x^34 + x^33 + x^31 + x^29 +
 * x^24 + x^23 + x^21 + x^19 + x^13 + x^9 + x^7 + x^6 + x^5 + 1 */

#define LFSR_SIZE        6 /* Bytes */

// Functions fa, fb and fc in filter output network. Definitions taken from Timo Kasper's thesis
#define FA(x3, x2, x1, x0) ( \
    ( (x0 | x1) ^ (x0 & x3) ) ^ ( x2 & ( (x0 ^ x1) | x3 ) ) \
)

#define FB(x3, x2, x1, x0) ( \
    ( (x0 & x1) | x2 ) ^ ( (x0 ^ x1) & (x2 | x3) ) \
)

#define FC(x4, x3, x2, x1, x0) ( \
    ( x0 | ( (x1 | x4) & (x3 ^ x4) ) ) ^ ( ( x0 ^ (x1 & x3) ) & ( (x2 ^ x3) | (x1 & x4) ) ) \
)

/* Platform independent code */

#define SPLIT_BYTE(__even, __odd, __byte) \
    __even = (__even >> 1) | (__byte<<7); __byte>>=1; \
    __odd  = (__odd  >> 1) | (__byte<<7); __byte>>=1; \
    __even = (__even >> 1) | (__byte<<7); __byte>>=1; \
    __odd  = (__odd  >> 1) | (__byte<<7); __byte>>=1; \
    __even = (__even >> 1) | (__byte<<7); __byte>>=1; \
    __odd  = (__odd  >> 1) | (__byte<<7); __byte>>=1; \
    __even = (__even >> 1) | (__byte<<7); __byte>>=1; \
    __odd  = (__odd  >> 1) | (__byte<<7)


/* Space/speed trade-off. */
/* We want speed, so we have to pay with size. */
/* If we combine the A and B filter tables and precalculate the values */
/* for each state byte, we get the following tables which gives a */
/* faster calculation of the filter output */
/* Table of the filter A/B output per byte */
/* NOTE: forced into `.data` so the tables live in RAM, cuts the dominant
 * cost of the crypto1 inner loop. Never written at runtime.
 */
#ifdef __APPLE__ // macOS
    #define CRYPTO1_LUT __attribute__((section("__DATA,__data")))
#else // Linux/Embedded
    #define CRYPTO1_LUT __attribute__((section(".data.crypto1")))
#endif
static const uint8_t abFilterTable0[16] CRYPTO1_LUT = {
    0x00, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x01,
    0x00, 0x00, 0x01, 0x00, 0x01, 0x01, 0x00, 0x01
};
static const uint8_t abFilterTable[2][256] CRYPTO1_LUT = {
    /* for Odd[1] */
    {
        0x00, 0x00, 0x00, 0x02, 0x02, 0x00, 0x00, 0x02,
        0x00, 0x02, 0x02, 0x02, 0x02, 0x00, 0x00, 0x02,
        0x00, 0x00, 0x00, 0x02, 0x02, 0x00, 0x00, 0x02,
        0x00, 0x02, 0x02, 0x02, 0x02, 0x00, 0x00, 0x02,
        0x00, 0x00, 0x00, 0x02, 0x02, 0x00, 0x00, 0x02,
        0x00, 0x02, 0x02, 0x02, 0x02, 0x00, 0x00, 0x02,
        0x04, 0x04, 0x04, 0x06, 0x06, 0x04, 0x04, 0x06,
        0x04, 0x06, 0x06, 0x06, 0x06, 0x04, 0x04, 0x06,
        0x04, 0x04, 0x04, 0x06, 0x06, 0x04, 0x04, 0x06,
        0x04, 0x06, 0x06, 0x06, 0x06, 0x04, 0x04, 0x06,
        0x00, 0x00, 0x00, 0x02, 0x02, 0x00, 0x00, 0x02,
        0x00, 0x02, 0x02, 0x02, 0x02, 0x00, 0x00, 0x02,
        0x00, 0x00, 0x00, 0x02, 0x02, 0x00, 0x00, 0x02,
        0x00, 0x02, 0x02, 0x02, 0x02, 0x00, 0x00, 0x02,
        0x04, 0x04, 0x04, 0x06, 0x06, 0x04, 0x04, 0x06,
        0x04, 0x06, 0x06, 0x06, 0x06, 0x04, 0x04, 0x06,
        0x00, 0x00, 0x00, 0x02, 0x02, 0x00, 0x00, 0x02,
        0x00, 0x02, 0x02, 0x02, 0x02, 0x00, 0x00, 0x02,
        0x04, 0x04, 0x04, 0x06, 0x06, 0x04, 0x04, 0x06,
        0x04, 0x06, 0x06, 0x06, 0x06, 0x04, 0x04, 0x06,
        0x04, 0x04, 0x04, 0x06, 0x06, 0x04, 0x04, 0x06,
        0x04, 0x06, 0x06, 0x06, 0x06, 0x04, 0x04, 0x06,
        0x04, 0x04, 0x04, 0x06, 0x06, 0x04, 0x04, 0x06,
        0x04, 0x06, 0x06, 0x06, 0x06, 0x04, 0x04, 0x06,
        0x04, 0x04, 0x04, 0x06, 0x06, 0x04, 0x04, 0x06,
        0x04, 0x06, 0x06, 0x06, 0x06, 0x04, 0x04, 0x06,
        0x00, 0x00, 0x00, 0x02, 0x02, 0x00, 0x00, 0x02,
        0x00, 0x02, 0x02, 0x02, 0x02, 0x00, 0x00, 0x02,
        0x00, 0x00, 0x00, 0x02, 0x02, 0x00, 0x00, 0x02,
        0x00, 0x02, 0x02, 0x02, 0x02, 0x00, 0x00, 0x02,
        0x04, 0x04, 0x04, 0x06, 0x06, 0x04, 0x04, 0x06,
        0x04, 0x06, 0x06, 0x06, 0x06, 0x04, 0x04, 0x06
    },
    /* for Odd[2] */
    {
        0x00, 0x08, 0x08, 0x08, 0x00, 0x00, 0x00, 0x08,
        0x00, 0x00, 0x08, 0x00, 0x08, 0x08, 0x00, 0x08,
        0x00, 0x08, 0x08, 0x08, 0x00, 0x00, 0x00, 0x08,
        0x00, 0x00, 0x08, 0x00, 0x08, 0x08, 0x00, 0x08,
        0x00, 0x08, 0x08, 0x08, 0x00, 0x00, 0x00, 0x08,
        0x00, 0x00, 0x08, 0x00, 0x08, 0x08, 0x00, 0x08,
        0x10, 0x18, 0x18, 0x18, 0x10, 0x10, 0x10, 0x18,
        0x10, 0x10, 0x18, 0x10, 0x18, 0x18, 0x10, 0x18,
        0x10, 0x18, 0x18, 0x18, 0x10, 0x10, 0x10, 0x18,
        0x10, 0x10, 0x18, 0x10, 0x18, 0x18, 0x10, 0x18,
        0x00, 0x08, 0x08, 0x08, 0x00, 0x00, 0x00, 0x08,
        0x00, 0x00, 0x08, 0x00, 0x08, 0x08, 0x00, 0x08,
        0x00, 0x08, 0x08, 0x08, 0x00, 0x00, 0x00, 0x08,
        0x00, 0x00, 0x08, 0x00, 0x08, 0x08, 0x00, 0x08,
        0x10, 0x18, 0x18, 0x18, 0x10, 0x10, 0x10, 0x18,
        0x10, 0x10, 0x18, 0x10, 0x18, 0x18, 0x10, 0x18,
        0x00, 0x08, 0x08, 0x08, 0x00, 0x00, 0x00, 0x08,
        0x00, 0x00, 0x08, 0x00, 0x08, 0x08, 0x00, 0x08,
        0x10, 0x18, 0x18, 0x18, 0x10, 0x10, 0x10, 0x18,
        0x10, 0x10, 0x18, 0x10, 0x18, 0x18, 0x10, 0x18,
        0x10, 0x18, 0x18, 0x18, 0x10, 0x10, 0x10, 0x18,
        0x10, 0x10, 0x18, 0x10, 0x18, 0x18, 0x10, 0x18,
        0x10, 0x18, 0x18, 0x18, 0x10, 0x10, 0x10, 0x18,
        0x10, 0x10, 0x18, 0x10, 0x18, 0x18, 0x10, 0x18,
        0x10, 0x18, 0x18, 0x18, 0x10, 0x10, 0x10, 0x18,
        0x10, 0x10, 0x18, 0x10, 0x18, 0x18, 0x10, 0x18,
        0x00, 0x08, 0x08, 0x08, 0x00, 0x00, 0x00, 0x08,
        0x00, 0x00, 0x08, 0x00, 0x08, 0x08, 0x00, 0x08,
        0x00, 0x08, 0x08, 0x08, 0x00, 0x00, 0x00, 0x08,
        0x00, 0x00, 0x08, 0x00, 0x08, 0x08, 0x00, 0x08,
        0x10, 0x18, 0x18, 0x18, 0x10, 0x10, 0x10, 0x18,
        0x10, 0x10, 0x18, 0x10, 0x18, 0x18, 0x10, 0x18
    }
};

/* Standard FC  table, feedback at bit 0 */
static const uint8_t TableC0[32] CRYPTO1_LUT = {
    /* fc with Input {4,3,2,1,0} = (0,0,0,0,0) to (1,1,1,1,1) */
    FC(0, 0, 0, 0, 0), FC(0, 0, 0, 0, 1), FC(0, 0, 0, 1, 0), FC(0, 0, 0, 1, 1),
    FC(0, 0, 1, 0, 0), FC(0, 0, 1, 0, 1), FC(0, 0, 1, 1, 0), FC(0, 0, 1, 1, 1),
    FC(0, 1, 0, 0, 0), FC(0, 1, 0, 0, 1), FC(0, 1, 0, 1, 0), FC(0, 1, 0, 1, 1),
    FC(0, 1, 1, 0, 0), FC(0, 1, 1, 0, 1), FC(0, 1, 1, 1, 0), FC(0, 1, 1, 1, 1),
    FC(1, 0, 0, 0, 0), FC(1, 0, 0, 0, 1), FC(1, 0, 0, 1, 0), FC(1, 0, 0, 1, 1),
    FC(1, 0, 1, 0, 0), FC(1, 0, 1, 0, 1), FC(1, 0, 1, 1, 0), FC(1, 0, 1, 1, 1),
    FC(1, 1, 0, 0, 0), FC(1, 1, 0, 0, 1), FC(1, 1, 0, 1, 0), FC(1, 1, 0, 1, 1),
    FC(1, 1, 1, 0, 0), FC(1, 1, 1, 0, 1), FC(1, 1, 1, 1, 0), FC(1, 1, 1, 1, 1)
};

/* Filter Output Macros — TableC0 returns the FC output as a 0/1 bit.
 * The B7/B3 variants are just a shift at the call site; folded into the
 * following OR by the compiler. */
#define CRYPTO1_FILTER_OUTPUT_B0_24(__O0, __O1, __O2) TableC0[ abFilterTable0[(__O0) >> 4] | \
                    abFilterTable[0][__O1] | \
                    abFilterTable[1][__O2]]

#define CRYPTO1_FILTER_OUTPUT_B7_24(__O0, __O1, __O2) \
    ((uint8_t)(CRYPTO1_FILTER_OUTPUT_B0_24(__O0, __O1, __O2) << 7))

#define CRYPTO1_FILTER_OUTPUT_B3_24(__O0, __O1, __O2) \
    ((uint8_t)(CRYPTO1_FILTER_OUTPUT_B0_24(__O0, __O1, __O2) << 3))

/* Extract bytes 0/1/2 from a packed 24-in-32 LFSR half-word. */
#define BYTE0(w) ((uint8_t)(w))
#define BYTE1(w) ((uint8_t)((w) >> 8))
#define BYTE2(w) ((uint8_t)((w) >> 16))

/* 24-bit right-shift with new bit into position 23. Only bit 0 of in_bit
 * matters (preserves SHIFT24's original semantic: __in<<7 into a uint8_t). */
static inline uint32_t Crypto1Shift24(uint32_t word, uint8_t in_bit) {
    return (word >> 1) | ((uint32_t)(in_bit & 1) << 23);
}

/* Conditional-decrypt variant used only by Crypto1SetupNested: XOR the
 * input bit with (stream & decrypt) BEFORE extracting bit 0, mirroring
 * SHIFT24_COND_DECRYPT: ((__in)^((__stream)&(__decrypt)))<<7. */
static inline uint32_t Crypto1Shift24CondDecrypt(uint32_t word, uint8_t in,
                                                 uint8_t stream, uint8_t decrypt) {
    uint8_t bit = in ^ (stream & decrypt);
    return Crypto1Shift24(word, bit);
}

/* LFSR feedback: XOR of tap-masked bits across both halves, folded to 1 bit.
 * Equivalent to the old byte-by-byte Crypto1LFSRbyteFeedback — XOR-parity is
 * linear so folding after masking on the whole word gives the same bit. */
static inline uint8_t Crypto1LFSRFeedback(uint32_t EvenWord, uint32_t OddWord) {
    uint32_t fb = (EvenWord & LFSR_MASK_EVEN) ^ (OddWord & LFSR_MASK_ODD);
    fb ^= fb >> 16;
    fb ^= fb >> 8;
    fb ^= fb >> 4;
    fb ^= fb >> 2;
    fb ^= fb >> 1;
    return (uint8_t)(fb & 1);
}

/* ============================================================================
 * Per-clock crypto1 primitive used by every keystream loop below.
 *
 * Semantics of C1_CLK / C1_CLK_ND:
 *   OUT holds the filter output for the CURRENT source half (0/1, B0 form).
 *   Each expansion folds OUT into KS at the MSB, updates TGT via feedback,
 *   then refreshes OUT with the filter output computed from the NEW TGT —
 *   which becomes the source of the NEXT clock. Callers therefore prime OUT
 *   once from OddWord before the byte loop; OUT then flows across bits AND
 *   across byte boundaries with no re-priming.
 *
 * Semantics of C1_CLK_AUTH:
 *   No keystream byte produced. The filter output for SRC is folded straight
 *   into feedback along with the fed-in bit IN.
 * ============================================================================ */

#define C1_CLK(TGT, SRC, KS, OUT, XI)  do {                                        \
    (KS)  = (uint8_t)(((KS) >> 1) | ((OUT) << 7));                                  \
    uint8_t _fb = Crypto1LFSRFeedback((TGT), (SRC)) ^ (uint8_t)(XI);                \
    (TGT) = Crypto1Shift24((TGT), _fb);                                             \
    (OUT) = CRYPTO1_FILTER_OUTPUT_B0_24(BYTE0(TGT), BYTE1(TGT), BYTE2(TGT));        \
} while (0)

#define C1_CLK_ND(TGT, SRC, KS, OUT, XI, DEC)  do {                                \
    (KS)  = (uint8_t)(((KS) >> 1) | ((OUT) << 7));                                  \
    uint8_t _fb = Crypto1LFSRFeedback((TGT), (SRC)) ^ (uint8_t)(XI);                \
    (TGT) = Crypto1Shift24CondDecrypt((TGT), _fb, (OUT), (DEC));                    \
    (OUT) = CRYPTO1_FILTER_OUTPUT_B0_24(BYTE0(TGT), BYTE1(TGT), BYTE2(TGT));        \
} while (0)

#define C1_CLK_AUTH(TGT, SRC, IN)  do {                                            \
    uint8_t _fs = CRYPTO1_FILTER_OUTPUT_B0_24(BYTE0(SRC), BYTE1(SRC), BYTE2(SRC));  \
    uint8_t _fb = Crypto1LFSRFeedback((TGT), (SRC)) ^ _fs ^ (uint8_t)(IN);          \
    (TGT) = Crypto1Shift24((TGT), _fb);                                             \
} while (0)

/* Split Crypto1 state into even and odd bits            */
/* to speed up the output filter network                 */
/* Put both into one struct to enable relative addressing */
typedef struct {
    uint32_t Even;   /* 24 significant bits: [0:7]=old Even[0], [8:15]=old Even[1], [16:23]=old Even[2] */
    uint32_t Odd;    /* same byte-lane layout as Even */
} Crypto1LfsrState_t;
static Crypto1LfsrState_t State = { 0, 0 };


/* Debug output of state */
void Crypto1GetState(uint8_t *pEven, uint8_t *pOdd) {
    if (pEven) {
        pEven[0] = BYTE0(State.Even);
        pEven[1] = BYTE1(State.Even);
        pEven[2] = BYTE2(State.Even);
    }
    if (pOdd) {
        pOdd[0] = BYTE0(State.Odd);
        pOdd[1] = BYTE1(State.Odd);
        pOdd[2] = BYTE2(State.Odd);
    }
}

/* Proceed LFSR by one clock cycle */
/* Prototype to force inlining */
static __inline__ void Crypto1LFSR(uint8_t In) __attribute__((always_inline));
static void Crypto1LFSR(uint8_t In) {
    uint32_t Temp = State.Even;
    uint8_t Feedback = Crypto1LFSRFeedback(State.Even, State.Odd);

    /* After one LFSR clock: new Even <- old Odd; new Odd <- old Even
     * right-shifted by 1 with Feedback into position 23. */
    Temp = Crypto1Shift24(Temp, Feedback);

    State.Even = State.Odd;
    State.Odd  = Temp;
}

uint8_t Crypto1FilterOutput(void) {
    return CRYPTO1_FILTER_OUTPUT_B0_24(BYTE0(State.Odd), BYTE1(State.Odd), BYTE2(State.Odd));
}

/* Setup LFSR split into odd and even states, feed in uid ^nonce */
/* Version for first (not nested) authentication.                 */
void Crypto1Setup(uint8_t Key[6], uint8_t Uid[4], uint8_t CardNonce[4]) {
    uint8_t Even0 = 0, Even1 = 0, Even2 = 0;
    uint8_t Odd0  = 0, Odd1  = 0, Odd2  = 0;
    uint8_t KeyStream;

    KeyStream = *Key++; SPLIT_BYTE(Even0, Odd0, KeyStream);
    KeyStream = *Key++; SPLIT_BYTE(Even0, Odd0, KeyStream);
    KeyStream = *Key++; SPLIT_BYTE(Even1, Odd1, KeyStream);
    KeyStream = *Key++; SPLIT_BYTE(Even1, Odd1, KeyStream);
    KeyStream = *Key++; SPLIT_BYTE(Even2, Odd2, KeyStream);
    KeyStream = *Key++; SPLIT_BYTE(Even2, Odd2, KeyStream);

    register uint32_t EvenWord = (uint32_t)Even0 | ((uint32_t)Even1 << 8) | ((uint32_t)Even2 << 16);
    register uint32_t OddWord  = (uint32_t)Odd0  | ((uint32_t)Odd1  << 8) | ((uint32_t)Odd2  << 16);
    uint8_t In, Out;

    /* Prime the filter output from Odd; C1_CLK carries OUT across bits AND
     * byte boundaries — no re-priming inside the loop. */
    Out = CRYPTO1_FILTER_OUTPUT_B0_24(BYTE0(OddWord), BYTE1(OddWord), BYTE2(OddWord));

    for (uint8_t ByteCount = 0; ByteCount < NONCE_SIZE; ByteCount++) {
        In = *CardNonce ^ *Uid++;
        C1_CLK(EvenWord, OddWord, KeyStream, Out, In); In >>= 1;
        C1_CLK(OddWord, EvenWord, KeyStream, Out, In); In >>= 1;
        C1_CLK(EvenWord, OddWord, KeyStream, Out, In); In >>= 1;
        C1_CLK(OddWord, EvenWord, KeyStream, Out, In); In >>= 1;
        C1_CLK(EvenWord, OddWord, KeyStream, Out, In); In >>= 1;
        C1_CLK(OddWord, EvenWord, KeyStream, Out, In); In >>= 1;
        C1_CLK(EvenWord, OddWord, KeyStream, Out, In); In >>= 1;
        C1_CLK(OddWord, EvenWord, KeyStream, Out, In);
        *CardNonce++ ^= KeyStream;
    }
    State.Even = EvenWord;
    State.Odd  = OddWord;
}

/* Setup LFSR split into odd and even states, feed in uid ^nonce    */
/* Version for nested authentication.                               */
/* Also generates encrypted parity bits at CardNonce[4]..[7]        */
/* Use: Decrypt = false for the tag, Decrypt = true for the reader  */
void Crypto1SetupNested(uint8_t Key[6], uint8_t Uid[4], uint8_t CardNonce[4], uint8_t NonceParity[4], bool Decrypt) {
    uint8_t Even0 = 0, Even1 = 0, Even2 = 0;
    uint8_t Odd0  = 0, Odd1  = 0, Odd2  = 0;
    uint8_t KeyStream;

    KeyStream = *Key++; SPLIT_BYTE(Even0, Odd0, KeyStream);
    KeyStream = *Key++; SPLIT_BYTE(Even0, Odd0, KeyStream);
    KeyStream = *Key++; SPLIT_BYTE(Even1, Odd1, KeyStream);
    KeyStream = *Key++; SPLIT_BYTE(Even1, Odd1, KeyStream);
    KeyStream = *Key++; SPLIT_BYTE(Even2, Odd2, KeyStream);
    KeyStream = *Key++; SPLIT_BYTE(Even2, Odd2, KeyStream);

    register uint32_t EvenWord = (uint32_t)Even0 | ((uint32_t)Even1 << 8) | ((uint32_t)Even2 << 16);
    register uint32_t OddWord  = (uint32_t)Odd0  | ((uint32_t)Odd1  << 8) | ((uint32_t)Odd2  << 16);
    uint8_t In, Out;
    uint8_t Dec = Decrypt ? 1 : 0;

    /* Prime filter output from Odd — flows across bits AND across byte
     * boundaries (parity-step Out becomes bit-0 Out of the next byte). */
    Out = CRYPTO1_FILTER_OUTPUT_B0_24(BYTE0(OddWord), BYTE1(OddWord), BYTE2(OddWord));

    for (uint8_t ByteCount = 0; ByteCount < NONCE_SIZE; ByteCount++) {
        In = *CardNonce ^ *Uid++;
        C1_CLK_ND(EvenWord, OddWord, KeyStream, Out, In, Dec); In >>= 1;
        C1_CLK_ND(OddWord, EvenWord, KeyStream, Out, In, Dec); In >>= 1;
        C1_CLK_ND(EvenWord, OddWord, KeyStream, Out, In, Dec); In >>= 1;
        C1_CLK_ND(OddWord, EvenWord, KeyStream, Out, In, Dec); In >>= 1;
        C1_CLK_ND(EvenWord, OddWord, KeyStream, Out, In, Dec); In >>= 1;
        C1_CLK_ND(OddWord, EvenWord, KeyStream, Out, In, Dec); In >>= 1;
        C1_CLK_ND(EvenWord, OddWord, KeyStream, Out, In, Dec); In >>= 1;
        C1_CLK_ND(OddWord, EvenWord, KeyStream, Out, In, Dec);

        /* Out already holds filter(Odd) after bit-7's shift — reuse for parity. */
        *NonceParity++ = Out ^ ODD_PARITY(*CardNonce);
        *CardNonce++ ^= KeyStream;
    }
    State.Even = EvenWord;
    State.Odd  = OddWord;
}

/* Crypto1Auth is similar to Crypto1Byte but */
/* EncryptedReaderNonce is decrypted and fed back */
void Crypto1Auth(uint8_t EncryptedReaderNonce[NONCE_SIZE]) {
    register uint32_t EvenWord = State.Even;
    register uint32_t OddWord  = State.Odd;

    for (uint8_t i = 0; i < NONCE_SIZE; i++) {
        uint8_t In = EncryptedReaderNonce[i];
        C1_CLK_AUTH(EvenWord, OddWord, In); In >>= 1;
        C1_CLK_AUTH(OddWord, EvenWord, In); In >>= 1;
        C1_CLK_AUTH(EvenWord, OddWord, In); In >>= 1;
        C1_CLK_AUTH(OddWord, EvenWord, In); In >>= 1;
        C1_CLK_AUTH(EvenWord, OddWord, In); In >>= 1;
        C1_CLK_AUTH(OddWord, EvenWord, In); In >>= 1;
        C1_CLK_AUTH(EvenWord, OddWord, In); In >>= 1;
        C1_CLK_AUTH(OddWord, EvenWord, In);
    }
    State.Even = EvenWord;
    State.Odd  = OddWord;
}

/* Crypto1Nibble generates keystream for a nibble (4 bit) */
/* no input to the LFSR  */
uint8_t Crypto1Nibble(void) {
    register uint32_t EvenWord = State.Even;
    register uint32_t OddWord  = State.Odd;
    uint8_t KeyStream = 0, Out;

    Out = CRYPTO1_FILTER_OUTPUT_B0_24(BYTE0(OddWord), BYTE1(OddWord), BYTE2(OddWord));
    C1_CLK(EvenWord, OddWord, KeyStream, Out, 0);
    C1_CLK(OddWord, EvenWord, KeyStream, Out, 0);
    C1_CLK(EvenWord, OddWord, KeyStream, Out, 0);
    C1_CLK(OddWord, EvenWord, KeyStream, Out, 0);

    State.Even = EvenWord;
    State.Odd  = OddWord;

    return KeyStream >> 4;
}

/* Crypto1Byte generates keystream for a byte (8 bit) */
/* no input to the LFSR  */
uint8_t Crypto1Byte(void) {
    register uint32_t EvenWord = State.Even;
    register uint32_t OddWord  = State.Odd;
    uint8_t KeyStream = 0, Out;

    Out = CRYPTO1_FILTER_OUTPUT_B0_24(BYTE0(OddWord), BYTE1(OddWord), BYTE2(OddWord));
    C1_CLK(EvenWord, OddWord, KeyStream, Out, 0);
    C1_CLK(OddWord, EvenWord, KeyStream, Out, 0);
    C1_CLK(EvenWord, OddWord, KeyStream, Out, 0);
    C1_CLK(OddWord, EvenWord, KeyStream, Out, 0);
    C1_CLK(EvenWord, OddWord, KeyStream, Out, 0);
    C1_CLK(OddWord, EvenWord, KeyStream, Out, 0);
    C1_CLK(EvenWord, OddWord, KeyStream, Out, 0);
    C1_CLK(OddWord, EvenWord, KeyStream, Out, 0);

    State.Even = EvenWord;
    State.Odd  = OddWord;
    return KeyStream;
}

/* Crypto1ByteArray transcripts array of bytes         */
/* No input to the LFSR                                */
/* Avoids load/store of the LFSR-state for each byte!  */
/* Enhancement for the original function Crypto1Byte() */
void Crypto1ByteArray(uint8_t *Buffer, uint8_t Count) {
    register uint32_t EvenWord = State.Even;
    register uint32_t OddWord  = State.Odd;
    uint8_t KeyStream = 0, Out;

    Out = CRYPTO1_FILTER_OUTPUT_B0_24(BYTE0(OddWord), BYTE1(OddWord), BYTE2(OddWord));
    while (Count--) {
        C1_CLK(EvenWord, OddWord, KeyStream, Out, 0);
        C1_CLK(OddWord, EvenWord, KeyStream, Out, 0);
        C1_CLK(EvenWord, OddWord, KeyStream, Out, 0);
        C1_CLK(OddWord, EvenWord, KeyStream, Out, 0);
        C1_CLK(EvenWord, OddWord, KeyStream, Out, 0);
        C1_CLK(OddWord, EvenWord, KeyStream, Out, 0);
        C1_CLK(EvenWord, OddWord, KeyStream, Out, 0);
        C1_CLK(OddWord, EvenWord, KeyStream, Out, 0);
        *Buffer++ ^= KeyStream;
    }
    State.Even = EvenWord;
    State.Odd  = OddWord;
}

/*
 * Fused encrypt + parity + wire-frame packer.
 * Produces the same bit-packed output as encrypting the buffer with its
 * parity bits and then packing it with
 *   nfc_tag_14a_wrap_frame(buf, count*8, par, frame);
 * but in a single pass, so `frame` can be fed straight to
 * nfc_tag_14a_tx_bits without an intermediate buffer + wrap step.
 */
static inline void Crypto1PackFrame9(uint8_t *Frame, uint16_t BitPos,
                                     uint8_t Data, uint8_t ParityBit) {
    uint32_t val = ((uint32_t)Data | ((uint32_t)(ParityBit & 1) << 8)) << (BitPos & 7);
    uint32_t off = BitPos >> 3;
    Frame[off]     |= (uint8_t)val;
    Frame[off + 1] |= (uint8_t)(val >> 8);
}

uint16_t Crypto1EncryptToFrame(uint8_t *Frame, uint8_t *Src, uint8_t Count, uint8_t CrcLen,
                                       uint8_t TriggerBytes, Crypto1StreamCb Cb) {
    register uint32_t EvenWord = State.Even;
    register uint32_t OddWord  = State.Odd;
    uint8_t KeyStream = 0, Out;
    uint16_t BitPos = 0;
    uint16_t Crc = crc_14a_init();
    const uint32_t Bits = Count * 9;
    bool CbFired = false;

    Out = CRYPTO1_FILTER_OUTPUT_B0_24(BYTE0(OddWord), BYTE1(OddWord), BYTE2(OddWord));
    for (uint8_t i = 0; i < Count; i++) {
        if (CrcLen > 0 && i == CrcLen) {
            /* All CrcLen leading bytes folded in — write the CRC_A now,
             * just before it's read below and encrypted like any other byte. */
            Src[CrcLen]     = (uint8_t)(Crc & 0xFF);
            Src[CrcLen + 1] = (uint8_t)(Crc >> 8);
        }
        uint8_t Plain = Src[i];
        if (i < CrcLen) {
            Crc = crc_14a_update(Crc, Plain);
        }

        C1_CLK(EvenWord, OddWord, KeyStream, Out, 0);
        C1_CLK(OddWord, EvenWord, KeyStream, Out, 0);
        C1_CLK(EvenWord, OddWord, KeyStream, Out, 0);
        C1_CLK(OddWord, EvenWord, KeyStream, Out, 0);
        C1_CLK(EvenWord, OddWord, KeyStream, Out, 0);
        C1_CLK(OddWord, EvenWord, KeyStream, Out, 0);
        C1_CLK(EvenWord, OddWord, KeyStream, Out, 0);
        C1_CLK(OddWord, EvenWord, KeyStream, Out, 0);

        /* Out holds filter(Odd) after bit 7 — the parity bit. */
        uint8_t Par    = ODD_PARITY(Plain) ^ Out;
        uint8_t Cipher = Plain ^ KeyStream;
        Crypto1PackFrame9(Frame, BitPos, Cipher, Par);
        BitPos += 9;

        /* Streaming: fire cb the first time we cross the trigger boundary.
         * DMB so any DMA started by cb sees all our writes. */
        if (!CbFired && Cb != NULL && (i + 1) >= TriggerBytes) {
            __DMB();
            Cb(Bits);
            CbFired = true;
        }
    }
    State.Even = EvenWord;
    State.Odd  = OddWord;
    return BitPos;
}

/* Function Crypto1PRNG                                           */
/* New version of the PRNG which can calculate multiple           */
/* feedback bits at once!                                         */
/* Feedback mask = 0x2d  = 101101 binary                          */
/* Because pattern 101 is repeated, only 2 shifts are necessary!  */
/*      Feedback ^= Feedback >> 3;     folds 101 101 to 101       */
/*      Feedback ^= Feedback >> 2;     folds 101 => 1             */
/* With these two lines not only bit 0 is calculated,             */
/* but all the bits which do no overlap with the feedback!        */
/* I.e. the 10 leading zeros in the feedback mask bits            */
/* gives us a total of 11 valid feedback bits!                    */
/* The ClockCount for the PRNG is always multiple of 32!          */
/* Up tp 11 Bits can be calculated at once                        */
/* Split into chunks of 11+11+10 = 32 bits                        */
/* This avoids a calculated number of shifts                      */
void Crypto1PRNG(uint8_t State[4], uint8_t ClockCount) {
    /* For ease of processing convert the state into a 32 bit integer first */
    uint32_t Temp;
    uint16_t Feedback;

    Temp  = (uint32_t) State[0] << 0;
    Temp |= (uint32_t) State[1] << 8;
    Temp |= (uint32_t) State[2] << 16;
    Temp |= (uint32_t) State[3] << 24;

    /* PRNG is always a multiple of 32!        */
    /* Up tp 11 Bits can be calculated at once */
    /* Split into chunks of 11+11+10 = 32 bits */
    while (ClockCount >= 32) {
        Feedback = (uint16_t)(Temp >> 16);
        Feedback ^= Feedback >> 3;   /* 2d = 101101,  fold 101 101 => 101 */
        Feedback ^= Feedback >> 2;   /* fold 101 => 1 */
        /* Cycle LFSR and feed back. */
        Temp = (Temp >> 11) | (((uint32_t)Feedback) << (32 - 11));

        /* Same for the next 11 Bits */
        Feedback = (uint16_t)(Temp >> 16);
        Feedback ^= Feedback >> 3;   /* 2d = 101101,  fold 101 101 => 101 */
        Feedback ^= Feedback >> 2;   /* fold 101 => 1 */
        Temp = (Temp >> 11) | (((uint32_t)Feedback) << (32 - 11));

        /* Remaining 10 bits */
        Feedback = (uint16_t)(Temp >> 16);
        Feedback ^= Feedback >> 3;   /* 2d = 101101,  fold 101 101 => 101 */
        Feedback ^= Feedback >> 2;   /* fold 101 => 1 */
        Temp = (Temp >> 10) | (((uint32_t)Feedback) << (32 - 10));

        /* Now 32 bits are fed back */
        ClockCount -= 32;
    }

    /* Store back state */
    State[0] = (uint8_t)(Temp >> 0);
    State[1] = (uint8_t)(Temp >> 8);
    State[2] = (uint8_t)(Temp >> 16);
    State[3] = (uint8_t)(Temp >> 24);
}

/* prng_successor
 * helper used to obscure the keystream during authentication
 */
uint32_t Crypto1FreePRNG(uint32_t x, uint32_t n) {
    x = __builtin_bswap32(x);

    while (n--)
        x = x >> 1 | (x >> 16 ^ x >> 18 ^ x >> 19 ^ x >> 21) << 31;

    return __builtin_bswap32(x);
}


void Crypto1EncryptWithParity(uint8_t *Buffer, uint8_t BitCount) {
    uint8_t i = 0;
    while (i < BitCount) {
        Buffer[i / 8] ^=
            CRYPTO1_FILTER_OUTPUT_B0_24(BYTE0(State.Odd), BYTE1(State.Odd), BYTE2(State.Odd))
            << (i % 8);
        if (++i % 9 != 0) // only shift, if this was no parity bit
            Crypto1LFSR(0);
    }
}

void Crypto1ReaderAuthWithParity(uint8_t PlainReaderAnswerWithParityBits[9]) {
    uint8_t i = 0, feedback;
    while (i < 72) {
        feedback = PlainReaderAnswerWithParityBits[i / 8] >> (i % 8);
        PlainReaderAnswerWithParityBits[i / 8] ^=
            CRYPTO1_FILTER_OUTPUT_B0_24(BYTE0(State.Odd), BYTE1(State.Odd), BYTE2(State.Odd))
            << (i % 8);
        if (++i % 9 != 0) { // only shift, if this was no parity bit
            if (i <= 36)
                Crypto1LFSR(feedback & 1);
            else
                Crypto1LFSR(0);
        }
    }
}
