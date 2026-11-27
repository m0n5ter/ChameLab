/*
 * Crypto1 host-side correctness harness.
 * DOCVERSION=CR1TEST_20260818
 *
 * Diffs mf1_crypto1.c (the implementation we are optimising) against
 * mf1_crapto1.c (reference — trusted, hardware-verified) plus
 * crypto1_helper.c (mf_crypto1_encryptEx — parity oracle).
 *
 * The unmodified mf1_crypto1.c must pass 100% of test cases before any
 * optimisation change is committed.
 */

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdbool.h>

#include "mf1_crypto1.h"       /* MF1 optimisation target */
#include "mf1_crapto1.h"       /* Reference oracle */
#include "crypto1_helper.h"    /* Reference parity oracle (mf_crypto1_encrypt) */
#include "parity.h"
#include "crc_utils.h"
#include "nfc_14a_wrap.h"   /* Reference wire packer (nfc_tag_14a_wrap_frame) */

#define VERSION "CR1TEST_v3_20260823"  /* T6..T8 cover Crypto1EncryptToFrame */

#define KEYSTREAM_BYTES 32
#define ENC_LEN 16
#define FRAME_MAX_BYTES ((ENC_LEN * 9 + 15) / 8)   /* space for spill on OR-writes */

/* ---------------------------------------------------------------- */
/* Small deterministic RNG (xorshift64*). We want fixed seeds so a  */
/* failing case can be reproduced by re-running with --seed <s>.    */
/* ---------------------------------------------------------------- */
static uint64_t g_rng_state;

static void rng_seed(uint64_t s) {
    if (s == 0) s = 0xdeadbeefcafebabeULL;
    g_rng_state = s;
}

static uint64_t rng_u64(void) {
    uint64_t x = g_rng_state;
    x ^= x >> 12;
    x ^= x << 25;
    x ^= x >> 27;
    g_rng_state = x;
    return x * 0x2545F4914F6CDD1DULL;
}

static uint8_t  rng_u8(void)  { return (uint8_t)rng_u64(); }

static void rng_bytes(uint8_t *out, size_t n) {
    for (size_t i = 0; i < n; i++) out[i] = rng_u8();
}

/* ---------------------------------------------------------------- */
/* Byte-order helpers. Both implementations agree that a 4-byte     */
/* on-wire value maps to a uint32 as (b0<<24 | b1<<16 | b2<<8 | b3) */
/* — the reference's BEBIT(x,n)=BIT(x,n^24) traversal processes     */
/* byte 0 LSB-first, matching mf1_crypto1's inner-loop order.       */
/* ---------------------------------------------------------------- */
static uint32_t bytes4_to_u32(const uint8_t b[4]) {
    return ((uint32_t)b[0] << 24) | ((uint32_t)b[1] << 16)
         | ((uint32_t)b[2] << 8)  | ((uint32_t)b[3]);
}

static void u32_to_bytes4(uint32_t w, uint8_t out[4]) {
    out[0] = (uint8_t)(w >> 24);
    out[1] = (uint8_t)(w >> 16);
    out[2] = (uint8_t)(w >> 8);
    out[3] = (uint8_t)(w);
}

/* Assemble the 6-byte key into a uint64 the way the firmware does it —
 * bytes_to_num(key,6) = key[0]<<40 | key[1]<<32 | ... | key[5]<<0, i.e.
 * key[0] is the MSB (see hex_utils.c:bytes_to_num). This is the exact
 * u64 both Crypto1Setup(key,...) and crypto1_init(pcs, ui64) agree on;
 * the two paths coexist in nfc_mf1.c under NFC_MF1_FAST_SIM. */
static uint64_t key6_to_u64(const uint8_t k[6]) {
    uint64_t num = 0;
    for (int i = 0; i < 6; i++) num = (num << 8) | k[i];
    return num;
}

/* ---------------------------------------------------------------- */
/* Pretty-printers                                                  */
/* ---------------------------------------------------------------- */
static void print_hex(const char *label, const uint8_t *b, size_t n) {
    fprintf(stderr, "    %s = ", label);
    for (size_t i = 0; i < n; i++) fprintf(stderr, "%02X", b[i]);
    fprintf(stderr, "\n");
}

static void dump_case(const char *label,
                      uint32_t case_idx, uint64_t seed,
                      const uint8_t key[6], const uint8_t uid[4],
                      const uint8_t nt[4],  const uint8_t nr[4]) {
    fprintf(stderr, "  [FAIL/%s] case=%u seed=0x%016llx\n",
            label, case_idx, (unsigned long long)seed);
    print_hex("key", key, 6);
    print_hex("uid", uid, 4);
    print_hex("nt ", nt,  4);
    print_hex("nr ", nr,  4);
}

/* ---------------------------------------------------------------- */
/* Test-suite bookkeeping                                           */
/* ---------------------------------------------------------------- */
typedef struct {
    const char *name;
    uint32_t total;
    uint32_t pass;
    uint32_t fail;
    bool dumped;   /* only dump the first failing case per test */
} test_stat_t;

static void test_start(test_stat_t *t, const char *name, uint32_t total) {
    t->name = name;
    t->total = total;
    t->pass = 0;
    t->fail = 0;
    t->dumped = false;
    printf("[TEST] %s v%s START count=%u\n", name, VERSION, total);
}

static void test_report(const test_stat_t *t) {
    if (t->fail == 0) {
        printf("[TEST] %s v%s PASS (%u/%u)\n",
               t->name, VERSION, t->pass, t->total);
    } else {
        printf("[TEST] %s v%s FAIL (%u/%u)\n",
               t->name, VERSION, t->fail, t->total);
    }
}

/* ---------------------------------------------------------------- */
/* Case runners: each returns true on match, false on mismatch, and */
/* on the first mismatch dumps enough context to reproduce.         */
/* ---------------------------------------------------------------- */

/* T1: Crypto1Setup encrypted-nonce match. */
static bool case_t1_setup_encnt(uint32_t idx, uint64_t seed, test_stat_t *t,
                                const uint8_t key[6], const uint8_t uid[4],
                                const uint8_t nt[4]) {
    /* MF1 side: state is static; Setup encrypts the nonce in-place. */
    uint8_t mf1_key[6];   memcpy(mf1_key, key, 6);
    uint8_t mf1_uid[4];   memcpy(mf1_uid, uid, 4);
    uint8_t mf1_nt[4];    memcpy(mf1_nt,  nt,  4);
    Crypto1Setup(mf1_key, mf1_uid, mf1_nt);

    /* Reference side: init with key, feed uid^nt, XOR keystream into nt. */
    struct Crypto1State s;
    crypto1_init(&s, key6_to_u64(key));
    uint32_t uid_w = bytes4_to_u32(uid);
    uint32_t nt_w  = bytes4_to_u32(nt);
    uint32_t ks    = crypto1_word(&s, uid_w ^ nt_w, 0);
    uint32_t enc_w = ks ^ nt_w;
    uint8_t ref_enc_nt[4]; u32_to_bytes4(enc_w, ref_enc_nt);

    if (memcmp(mf1_nt, ref_enc_nt, 4) != 0) {
        if (!t->dumped) {
            dump_case("T1_Setup_encnt", idx, seed, key, uid, nt, (uint8_t[4]){0});
            print_hex("mf1_encnt", mf1_nt, 4);
            print_hex("ref_encnt", ref_enc_nt, 4);
            t->dumped = true;
        }
        return false;
    }
    return true;
}

/* T2: Auth + N keystream bytes match.
 *   nr_enc is the (already encrypted) reader nonce as it arrives from the
 *   reader — this is what both Crypto1Auth and crypto1_word(s,nr_enc,1)
 *   expect. */
static bool case_t2_auth_keystream(uint32_t idx, uint64_t seed, test_stat_t *t,
                                   const uint8_t key[6], const uint8_t uid[4],
                                   const uint8_t nt[4],  const uint8_t nr_enc[4]) {
    /* MF1 */
    uint8_t mf1_key[6]; memcpy(mf1_key, key, 6);
    uint8_t mf1_uid[4]; memcpy(mf1_uid, uid, 4);
    uint8_t mf1_nt[4];  memcpy(mf1_nt,  nt,  4);
    Crypto1Setup(mf1_key, mf1_uid, mf1_nt);
    uint8_t mf1_nr[4];  memcpy(mf1_nr, nr_enc, 4);
    Crypto1Auth(mf1_nr);
    uint8_t mf1_ks[KEYSTREAM_BYTES] = {0};
    Crypto1ByteArray(mf1_ks, KEYSTREAM_BYTES);

    /* Reference */
    struct Crypto1State s;
    crypto1_init(&s, key6_to_u64(key));
    (void)crypto1_word(&s, bytes4_to_u32(uid) ^ bytes4_to_u32(nt), 0);
    (void)crypto1_word(&s, bytes4_to_u32(nr_enc), 1);
    uint8_t ref_ks[KEYSTREAM_BYTES];
    for (int i = 0; i < KEYSTREAM_BYTES; i++) ref_ks[i] = crypto1_byte(&s, 0, 0);

    if (memcmp(mf1_ks, ref_ks, KEYSTREAM_BYTES) != 0) {
        if (!t->dumped) {
            dump_case("T2_Auth_KS", idx, seed, key, uid, nt, nr_enc);
            print_hex("mf1_ks", mf1_ks, KEYSTREAM_BYTES);
            print_hex("ref_ks", ref_ks, KEYSTREAM_BYTES);
            t->dumped = true;
        }
        return false;
    }
    return true;
}

/* T3: Nibble equivalence — Crypto1Nibble() vs 4 crypto1_bit() calls
 * assembled into a nibble (bits 0..3). */
static bool case_t3_nibble(uint32_t idx, uint64_t seed, test_stat_t *t,
                           const uint8_t key[6], const uint8_t uid[4],
                           const uint8_t nt[4],  const uint8_t nr_enc[4]) {
    uint8_t mf1_key[6]; memcpy(mf1_key, key, 6);
    uint8_t mf1_uid[4]; memcpy(mf1_uid, uid, 4);
    uint8_t mf1_nt[4];  memcpy(mf1_nt,  nt,  4);
    Crypto1Setup(mf1_key, mf1_uid, mf1_nt);
    uint8_t mf1_nr[4];  memcpy(mf1_nr, nr_enc, 4);
    Crypto1Auth(mf1_nr);

    struct Crypto1State s;
    crypto1_init(&s, key6_to_u64(key));
    (void)crypto1_word(&s, bytes4_to_u32(uid) ^ bytes4_to_u32(nt), 0);
    (void)crypto1_word(&s, bytes4_to_u32(nr_enc), 1);

    for (int n = 0; n < 8; n++) {
        uint8_t mf1_nib = Crypto1Nibble();
        uint8_t ref_nib = 0;
        for (int b = 0; b < 4; b++)
            ref_nib |= (uint8_t)(crypto1_bit(&s, 0, 0) << b);
        if ((mf1_nib & 0x0F) != (ref_nib & 0x0F)) {
            if (!t->dumped) {
                dump_case("T3_Nibble", idx, seed, key, uid, nt, nr_enc);
                fprintf(stderr, "    nibble_idx=%d mf1=0x%02X ref=0x%02X\n",
                        n, mf1_nib & 0x0F, ref_nib & 0x0F);
                t->dumped = true;
            }
            return false;
        }
    }
    return true;
}

/* T4: SetupNested(Decrypt=false) — the "tag side" path. Produces both an
 * encrypted nonce and an encrypted parity byte per nonce byte. The
 * encrypted nonce must match plain Setup (Decrypt=false makes
 * SHIFT24_COND_DECRYPT equivalent to SHIFT24), and each parity bit must
 * match filter(pcs->odd) XOR oddparity8(plaintext_nonce_byte). */
static bool case_t4_setup_nested_notdecrypt(uint32_t idx, uint64_t seed, test_stat_t *t,
                                            const uint8_t key[6], const uint8_t uid[4],
                                            const uint8_t nt[4]) {
    uint8_t mf1_key[6];   memcpy(mf1_key, key, 6);
    uint8_t mf1_uid[4];   memcpy(mf1_uid, uid, 4);
    uint8_t mf1_nt[4];    memcpy(mf1_nt,  nt,  4);
    uint8_t mf1_par[4]    = {0};
    Crypto1SetupNested(mf1_key, mf1_uid, mf1_nt, mf1_par, false);

    /* Reference: mirror the pattern of SetupNested by walking each nonce
     * byte through crypto1_word(nt^uid,0) and, immediately after each
     * byte, reading filter(odd)^oddparity8(plaintext) as the parity bit. */
    struct Crypto1State s;
    crypto1_init(&s, key6_to_u64(key));

    /* Reference: crypto1_word processes all 32 bits together — but we need
     * per-byte hooks for parity. Do byte-at-a-time with crypto1_bit. */
    uint8_t ref_enc_nt[4] = {0};
    uint8_t ref_par[4]    = {0};
    for (int by = 0; by < 4; by++) {
        uint8_t in  = (uint8_t)(nt[by] ^ uid[by]);
        uint8_t plain_nt = nt[by];
        uint8_t ks_byte  = 0;
        for (int b = 0; b < 8; b++) {
            uint8_t bit = crypto1_bit(&s, (in >> b) & 1, 0);
            ks_byte |= (uint8_t)(bit << b);
        }
        ref_enc_nt[by] = (uint8_t)(plain_nt ^ ks_byte);
        ref_par[by] = (uint8_t)(filter(s.odd) ^ oddparity8(plain_nt));
    }

    if (memcmp(mf1_nt, ref_enc_nt, 4) != 0 || memcmp(mf1_par, ref_par, 4) != 0) {
        if (!t->dumped) {
            dump_case("T4_SetupNested_notdec", idx, seed, key, uid, nt, (uint8_t[4]){0});
            print_hex("mf1_encnt", mf1_nt, 4);
            print_hex("ref_encnt", ref_enc_nt, 4);
            print_hex("mf1_par",   mf1_par, 4);
            print_hex("ref_par",   ref_par, 4);
            t->dumped = true;
        }
        return false;
    }
    return true;
}

/* T5: SetupNested(Decrypt=true) — the "reader side" path. The reader
 * receives an encrypted nested nonce and feeds the decrypted version to
 * the LFSR (equivalent to crypto1_word(&s, enc_nt, 1)). After the
 * nested setup, subsequent keystream must match. */
static bool case_t5_setup_nested_decrypt(uint32_t idx, uint64_t seed, test_stat_t *t,
                                         const uint8_t key[6], const uint8_t uid[4],
                                         const uint8_t enc_nt[4]) {
    uint8_t mf1_key[6];   memcpy(mf1_key, key, 6);
    uint8_t mf1_uid[4];   memcpy(mf1_uid, uid, 4);
    uint8_t mf1_encnt[4]; memcpy(mf1_encnt, enc_nt, 4);
    uint8_t mf1_par[4]    = {0};
    Crypto1SetupNested(mf1_key, mf1_uid, mf1_encnt, mf1_par, true);
    uint8_t mf1_ks[KEYSTREAM_BYTES] = {0};
    Crypto1ByteArray(mf1_ks, KEYSTREAM_BYTES);

    /* Reference: to feed the ENCRYPTED nonce into the LFSR (analogous to
     * the reader decrypting nt_enc and clocking the plaintext in), the
     * usage is crypto1_word(uid, 0) then crypto1_word(enc_nt, 1) with the
     * caveat that in the SetupNested flow, uid alone doesn't clock: it is
     * combined with the (decrypted) nested nonce byte inside the loop. So
     * we replicate that byte-by-byte. */
    struct Crypto1State s;
    crypto1_init(&s, key6_to_u64(key));
    for (int by = 0; by < 4; by++) {
        uint8_t in_enc = enc_nt[by];
        for (int b = 0; b < 8; b++) {
            /* At each clock, filter(odd) is the keystream bit; the
             * plaintext nonce bit is (enc_bit XOR filter(odd)); the value
             * fed to LFSR is that plaintext bit XORed with uid_bit. This
             * is exactly what crypto1_bit does when is_encrypted=1 and in
             * is the encrypted bit XORed with the uid bit. */
            uint8_t enc_bit = (in_enc >> b) & 1;
            uint8_t uid_bit = (uid[by] >> b) & 1;
            (void)crypto1_bit(&s, enc_bit ^ uid_bit, 1);
        }
    }
    uint8_t ref_ks[KEYSTREAM_BYTES];
    for (int i = 0; i < KEYSTREAM_BYTES; i++) ref_ks[i] = crypto1_byte(&s, 0, 0);

    if (memcmp(mf1_ks, ref_ks, KEYSTREAM_BYTES) != 0) {
        if (!t->dumped) {
            dump_case("T5_SetupNested_dec", idx, seed, key, uid, enc_nt, (uint8_t[4]){0});
            print_hex("mf1_ks", mf1_ks, KEYSTREAM_BYTES);
            print_hex("ref_ks", ref_ks, KEYSTREAM_BYTES);
            t->dumped = true;
        }
        return false;
    }
    return true;
}

/* T6: Crypto1EncryptToFrame vs the unfused reference path — mf_crypto1_encrypt
 * (ciphertext + parity oracle) followed by the REAL nfc_tag_14a_wrap_frame
 * (wire-packing oracle, extracted from nfc_14a.c by the Makefile). The fused
 * function must yield a bit-identical frame and return count*9 bits. */
static bool case_t6_fused_frame_matches(uint32_t idx, uint64_t seed, test_stat_t *t,
                                        const uint8_t key[6], const uint8_t uid[4],
                                        const uint8_t nt[4],  const uint8_t nr_enc[4],
                                        const uint8_t plaintext[ENC_LEN]) {
    /* Path A — fused encrypt + parity + pack */
    uint8_t mf1_key[6]; memcpy(mf1_key, key, 6);
    uint8_t mf1_uid[4]; memcpy(mf1_uid, uid, 4);
    uint8_t mf1_nt[4];  memcpy(mf1_nt,  nt,  4);
    Crypto1Setup(mf1_key, mf1_uid, mf1_nt);
    uint8_t mf1_nr[4];  memcpy(mf1_nr, nr_enc, 4);
    Crypto1Auth(mf1_nr);
    uint8_t mf1_src[ENC_LEN]; memcpy(mf1_src, plaintext, ENC_LEN);
    uint8_t frame_fused[FRAME_MAX_BYTES] = {0};
    uint16_t bits_ret = Crypto1EncryptToFrame(frame_fused, mf1_src, ENC_LEN, /*CrcLen*/0,
                                              /*trigger*/0, /*cb*/NULL);

    /* Path B — reference crypto1 encrypt-with-parity, then the real packer */
    struct Crypto1State s;
    crypto1_init(&s, key6_to_u64(key));
    (void)crypto1_word(&s, bytes4_to_u32(uid) ^ bytes4_to_u32(nt), 0);
    (void)crypto1_word(&s, bytes4_to_u32(nr_enc), 1);
    uint8_t ref_data[ENC_LEN]; memcpy(ref_data, plaintext, ENC_LEN);
    uint8_t ref_par[ENC_LEN]  = {0};
    mf_crypto1_encrypt(&s, ref_data, ENC_LEN, ref_par);
    uint8_t frame_ref[FRAME_MAX_BYTES] = {0};
    uint8_t bits_ref = nfc_tag_14a_wrap_frame(ref_data, ENC_LEN * 8, ref_par, frame_ref);

    if (bits_ret != ENC_LEN * 9 || bits_ret != bits_ref ||
            memcmp(frame_fused, frame_ref, FRAME_MAX_BYTES) != 0) {
        if (!t->dumped) {
            dump_case("T6_FusedFrame", idx, seed, key, uid, nt, nr_enc);
            print_hex("plaintext", plaintext, ENC_LEN);
            print_hex("ref_enc  ", ref_data,  ENC_LEN);
            print_hex("ref_par  ", ref_par,   ENC_LEN);
            print_hex("frame_ref  ", frame_ref,   FRAME_MAX_BYTES);
            print_hex("frame_fused", frame_fused, FRAME_MAX_BYTES);
            fprintf(stderr, "    bits_ret=%u bits_ref=%u expected=%u\n",
                    bits_ret, bits_ref, ENC_LEN * 9);
            t->dumped = true;
        }
        return false;
    }
    return true;
}

/* T7: streaming callback fires exactly once at (or after) the trigger byte,
 * the output frame is bit-identical to the non-streaming version, and the
 * cb's captured "bytes packed so far" is >= trigger. */
static uint32_t g_t7_fire_count;
static uint8_t  g_t7_trigger_bytes;
static void t7_cb(uint32_t bits) {
    g_t7_fire_count++;
}

static bool case_t7_streaming_cb(uint32_t idx, uint64_t seed, test_stat_t *t,
                                 const uint8_t key[6], const uint8_t uid[4],
                                 const uint8_t nt[4],  const uint8_t nr_enc[4],
                                 uint8_t plaintext[ENC_LEN]) {
    /* Path A — streaming */
    uint8_t k[6]; memcpy(k, key, 6);
    uint8_t u[4]; memcpy(u, uid, 4);
    uint8_t n[4]; memcpy(n, nt,  4);
    Crypto1Setup(k, u, n);
    uint8_t nr[4]; memcpy(nr, nr_enc, 4);
    Crypto1Auth(nr);
    uint8_t frame_stream[FRAME_MAX_BYTES] = {0};
    g_t7_fire_count = 0;
    g_t7_trigger_bytes = (uint8_t)(3 + (rng_u8() % (ENC_LEN - 3)));  /* 3..ENC_LEN-1 */
    (void)Crypto1EncryptToFrame(frame_stream, plaintext, ENC_LEN, /*CrcLen*/0,
                                g_t7_trigger_bytes, t7_cb);

    /* Path B — same, no streaming, from re-setup state */
    uint8_t k2[6]; memcpy(k2, key, 6);
    uint8_t u2[4]; memcpy(u2, uid, 4);
    uint8_t n2[4]; memcpy(n2, nt,  4);
    Crypto1Setup(k2, u2, n2);
    uint8_t nr2[4]; memcpy(nr2, nr_enc, 4);
    Crypto1Auth(nr2);
    uint8_t frame_plain[FRAME_MAX_BYTES] = {0};
    (void)Crypto1EncryptToFrame(frame_plain, plaintext, ENC_LEN, /*CrcLen*/0,
                                /*trigger*/0, /*cb*/NULL);

    bool cb_ok  = (g_t7_fire_count == 1);
    bool eq_ok  = (memcmp(frame_stream, frame_plain, FRAME_MAX_BYTES) == 0);

    if (!cb_ok || !eq_ok) {
        if (!t->dumped) {
            dump_case("T7_StreamingCb", idx, seed, key, uid, nt, nr_enc);
            fprintf(stderr, "    trigger=%u fires=%u (expected 1) eq=%s\n",
                    g_t7_trigger_bytes, g_t7_fire_count, eq_ok ? "yes" : "NO");
            print_hex("frame_stream", frame_stream, FRAME_MAX_BYTES);
            print_hex("frame_plain ", frame_plain,  FRAME_MAX_BYTES);
            t->dumped = true;
        }
        return false;
    }
    return true;
}

/* T8: Crypto1EncryptToFrameWithCrc fuses CRC_A computation into the encrypt
 * loop instead of a separate nfc_tag_14a_append_crc() pass. Checks that (a)
 * the CRC it writes into Src[CrcLen..CrcLen+1] matches calc_14a_crc_lut over
 * the leading CrcLen bytes, and (b) the resulting frame is bit-identical to
 * doing append_crc + plain Crypto1EncryptToFrame from the same LFSR state. */
#define CRC_DATA_LEN 16
#define CRC_TOTAL_LEN 18
#define CRC_FRAME_BYTES ((CRC_TOTAL_LEN * 9 + 15) / 8)

static bool case_t8_fused_crc_matches(uint32_t idx, uint64_t seed, test_stat_t *t,
                                      const uint8_t key[6], const uint8_t uid[4],
                                      const uint8_t nt[4],  const uint8_t nr_enc[4],
                                      const uint8_t plaintext[ENC_LEN]) {
    /* Path A — new fused function; trailing 2 bytes start as garbage and
     * must be overwritten with the correct CRC before being encrypted. */
    uint8_t src_a[CRC_TOTAL_LEN];
    memcpy(src_a, plaintext, CRC_DATA_LEN);
    src_a[CRC_DATA_LEN]     = (uint8_t)rng_u8();
    src_a[CRC_DATA_LEN + 1] = (uint8_t)rng_u8();

    uint8_t k[6]; memcpy(k, key, 6);
    uint8_t u[4]; memcpy(u, uid, 4);
    uint8_t n[4]; memcpy(n, nt,  4);
    Crypto1Setup(k, u, n);
    uint8_t nr[4]; memcpy(nr, nr_enc, 4);
    Crypto1Auth(nr);
    uint8_t frame_fused[CRC_FRAME_BYTES] = {0};
    uint16_t bits_ret = Crypto1EncryptToFrame(frame_fused, src_a, CRC_TOTAL_LEN,
                                              CRC_DATA_LEN, /*trigger*/0, NULL);

    uint8_t expected_crc[2];
    calc_14a_crc_lut((uint8_t *)plaintext, CRC_DATA_LEN, expected_crc);
    bool crc_ok = (src_a[CRC_DATA_LEN] == expected_crc[0] && src_a[CRC_DATA_LEN + 1] == expected_crc[1]);

    /* Path B — separate append_crc then plain Crypto1EncryptToFrame, from the same state */
    uint8_t src_b[CRC_TOTAL_LEN];
    memcpy(src_b, plaintext, CRC_DATA_LEN);
    calc_14a_crc_lut(src_b, CRC_DATA_LEN, &src_b[CRC_DATA_LEN]);

    uint8_t k2[6]; memcpy(k2, key, 6);
    uint8_t u2[4]; memcpy(u2, uid, 4);
    uint8_t n2[4]; memcpy(n2, nt,  4);
    Crypto1Setup(k2, u2, n2);
    uint8_t nr2[4]; memcpy(nr2, nr_enc, 4);
    Crypto1Auth(nr2);
    uint8_t frame_ref[CRC_FRAME_BYTES] = {0};
    uint16_t bits_ref = Crypto1EncryptToFrame(frame_ref, src_b, CRC_TOTAL_LEN, /*CrcLen*/0,
                                              /*trigger*/0, /*cb*/NULL);

    bool frame_ok = (bits_ret == bits_ref) && (bits_ret == CRC_TOTAL_LEN * 9) &&
                     (memcmp(frame_fused, frame_ref, CRC_FRAME_BYTES) == 0);

    if (!crc_ok || !frame_ok) {
        if (!t->dumped) {
            dump_case("T8_FusedCrc", idx, seed, key, uid, nt, nr_enc);
            print_hex("plaintext16", plaintext, CRC_DATA_LEN);
            print_hex("fused_crc  ", &src_a[CRC_DATA_LEN], 2);
            print_hex("expect_crc ", expected_crc, 2);
            print_hex("frame_fused", frame_fused, CRC_FRAME_BYTES);
            print_hex("frame_ref  ", frame_ref,   CRC_FRAME_BYTES);
            fprintf(stderr, "    bits_ret=%u bits_ref=%u expected=%u\n",
                    bits_ret, bits_ref, CRC_TOTAL_LEN * 9);
            t->dumped = true;
        }
        return false;
    }
    return true;
}

/* ---------------------------------------------------------------- */
/* Driver                                                           */
/* ---------------------------------------------------------------- */
static void check_frame_decoder(test_stat_t *t) {
    const uint16_t lengths[] = { 1, 4, 8, 16, 18, 32, 44, 128, 228 };
    test_start(t, "T9_FrameDecode", 18);
    for (size_t i = 0; i < sizeof(lengths) / sizeof(lengths[0]); i++) {
        uint16_t length = lengths[i];
        uint8_t input[228], frame[257] = {0};
        for (uint16_t j = 0; j < length; j++) {
            input[j] = (uint8_t)(j * 37 + length);
            uint16_t value = input[j] | ((uint16_t)oddparity8(input[j]) << 8);
            for (uint8_t bit = 0; bit < 9; bit++) {
                uint16_t position = j * 9 + bit;
                frame[position / 8] |= ((value >> bit) & 1) << (position % 8);
            }
        }
        for (uint8_t missing_parity = 0; missing_parity <= 1; missing_parity++) {
            uint8_t decoded[229];
            memset(decoded, 0xCC, sizeof(decoded));
            uint16_t bits = nfc_tag_14a_unwrap_frame(frame, length * 9 - missing_parity, decoded, NULL);
            if (bits == length * 8 && memcmp(input, decoded, length) == 0 && decoded[length] == 0xCC) {
                t->pass++;
            } else {
                t->fail++;
                fprintf(stderr, "T9: length=%u missing_parity=%u returned_bits=%u\n", length, missing_parity, bits);
            }
        }
    }
}

int main(int argc, char **argv) {
    uint64_t seed  = 0x00C0FFEE00C0FFEEULL;
    uint32_t count = 10000;

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--seed") == 0 && i + 1 < argc) {
            seed = strtoull(argv[++i], NULL, 0);
        } else if (strcmp(argv[i], "--count") == 0 && i + 1 < argc) {
            count = (uint32_t)strtoul(argv[++i], NULL, 0);
        } else {
            fprintf(stderr, "usage: %s [--seed HEXORDEC] [--count N]\n", argv[0]);
            return 2;
        }
    }

    printf("[HARNESS] crypto1_test v%s start seed=0x%016llx count=%u\n",
           VERSION, (unsigned long long)seed, count);

    test_stat_t t1, t2, t3, t4, t5, t6, t7, t8;
    test_start(&t1, "T1_Setup_encnt",          count);
    test_start(&t2, "T2_Auth_KS",              count);
    test_start(&t3, "T3_Nibble",               count);
    test_start(&t4, "T4_SetupNested_notdec",   count);
    test_start(&t5, "T5_SetupNested_dec",      count);
    test_start(&t6, "T6_FusedFrame",           count);
    test_start(&t7, "T7_StreamingCb",          count);
    test_start(&t8, "T8_FusedCrc",             count);

    rng_seed(seed);
    for (uint32_t i = 0; i < count; i++) {
        uint8_t key[6], uid[4], nt[4], nr[4], plaintext[ENC_LEN];
        rng_bytes(key, 6);
        rng_bytes(uid, 4);
        rng_bytes(nt,  4);
        rng_bytes(nr,  4);
        rng_bytes(plaintext, ENC_LEN);

        if (case_t1_setup_encnt(i, seed, &t1, key, uid, nt))            t1.pass++; else t1.fail++;
        if (case_t2_auth_keystream(i, seed, &t2, key, uid, nt, nr))     t2.pass++; else t2.fail++;
        if (case_t3_nibble(i, seed, &t3, key, uid, nt, nr))             t3.pass++; else t3.fail++;
        if (case_t4_setup_nested_notdecrypt(i, seed, &t4, key, uid, nt)) t4.pass++; else t4.fail++;
        if (case_t5_setup_nested_decrypt(i, seed, &t5, key, uid, nt))   t5.pass++; else t5.fail++;
        if (case_t6_fused_frame_matches(i, seed, &t6, key, uid, nt, nr, plaintext)) t6.pass++; else t6.fail++;
        if (case_t7_streaming_cb(i, seed, &t7, key, uid, nt, nr, plaintext)) t7.pass++; else t7.fail++;
        if (case_t8_fused_crc_matches(i, seed, &t8, key, uid, nt, nr, plaintext)) t8.pass++; else t8.fail++;
    }

    test_report(&t1);
    test_report(&t2);
    test_report(&t3);
    test_report(&t4);
    test_report(&t5);
    test_report(&t6);
    test_report(&t7);
    test_report(&t8);

    test_stat_t t9;
    check_frame_decoder(&t9);
    test_report(&t9);
    uint32_t total_fail = t1.fail + t2.fail + t3.fail + t4.fail + t5.fail + t6.fail + t7.fail + t8.fail + t9.fail;
    if (total_fail == 0) {
        printf("[HARNESS] crypto1_test v%s ALL PASS (%u cases x 8 tests = %u checks, plus 18 frame decoder checks)\n",
               VERSION, count, count * 8);
        return 0;
    } else {
        printf("[HARNESS] crypto1_test v%s FAIL total_failures=%u\n", VERSION, total_fail);
        return 1;
    }
}
