# crypto1 host-side correctness harness

Adapted from [unkernet/ChameleonUltra, commit 2f589d9](https://github.com/unkernet/ChameleonUltra/commit/2f589d95c0b5b8a4d9b4b502766f1b0f9a0eb9bd).

Diffs `mf1_crypto1.c` (the implementation being optimised) against
`mf1_crapto1.c` and `crypto1_helper.c` (reference oracles, hardware-verified)
on the host — no MCU required.

## Build & run

```
cd firmware/tests/crypto1
make
./build/test_crypto1                       # default: 10000 cases, fixed seed
./build/test_crypto1 --count 50000         # more cases
./build/test_crypto1 --seed 0xDEADBEEF     # different reproducible seed
```

Exit code is 0 on ALL PASS, 1 otherwise. On failure, the first failing case
per test is dumped (key/uid/nt/nr and both sides' output) so it can be
reproduced by re-running with the same `--seed`.

## Test coverage per case (8 comparisons)

- **T1** `Crypto1Setup` encrypted nonce vs `crypto1_init` + `crypto1_word(uid^nt, 0)`.
- **T2** `Crypto1Auth` + `Crypto1Byte`×32 vs `crypto1_word(nr, 1)` + `crypto1_byte`×32.
- **T3** `Crypto1Nibble` vs 4 `crypto1_bit` calls stitched into a nibble.
- **T4** `Crypto1SetupNested(Decrypt=false)` — encrypted nonce **and** parity bits vs `filter(pcs->odd) ^ oddparity8(plaintext)`.
- **T5** `Crypto1SetupNested(Decrypt=true)` — reader-side path — subsequent keystream vs bit-by-bit reference.
- **T6** `Crypto1EncryptToFrame` vs the unfused path it replaces: `mf_crypto1_encrypt` (ciphertext + parity) followed by `nfc_tag_14a_wrap_frame` (wire packing) — frame bits and return value.
- **T7** `Crypto1EncryptToFrame` streaming callback — fires exactly once, frame identical to the non-streaming call.
- **T8** `Crypto1EncryptToFrame` with `CrcLen > 0` — fused CRC_A matches `calc_14a_crc_lut`, frame identical to append-CRC-then-encrypt.

The run also checks **T9**: the production frame decoder on 1–228 data bytes,
with and without the final parity bit. It verifies 16-bit lengths and an output
canary to catch extra decoded bytes.

`nfc_14a.c` cannot be host-compiled (nrfx / NRF_LOG), so the Makefile extracts
`nfc_tag_14a_wrap_frame` from it into its own translation unit — the oracle is
the real production packer, not a copy.

The harness covers the cipher and packed frame output. RF timing and the
TIMER1/PPI RX streaming path require hardware validation; TIMER3 remains reserved
for ChameLab's hardware-timed HF Sniff.
