# Independent C reference vectors

`crypto_vectors.c` generates synthetic authentication data with the original
`software/src/crypto1.c`, not with the managed implementation being tested.
It also runs the original Darkside solver (`mfkey.c` / `crapto1.c`), including
the intersection of two parity-zero captures. Incompatible Darkside input is
kept as a negative vector when the original C returns no key.

`generate_vectors.py` additionally invokes the original `nested`, `staticnested`
and `mfkey32v2` executables. It records the full repeated-candidate lists from
Nested and confirms the expected keys with the other two executables.
The managed tests consume the saved JSON; a C compiler is not required to run them.

To regenerate, build the original executables with the repository's native
toolchain. Compile `crypto_vectors.c` with `crypto1.c`, `crapto1.c`,
`bucketsort.c`, `parity.c` and `mfkey.c`, adding `software/src` to the include path.
Then run:

```text
python generate_vectors.py --generator PATH/crypto_vectors.exe --nested PATH/nested.exe --staticnested PATH/staticnested.exe --mfkey32v2 PATH/mfkey32v2.exe
```

The audit used MSVC C11 with optimization. The original Nested wrappers were
built with a local synchronous `pthread_create`/`pthread_join` compatibility shim;
the algorithm sources were unchanged. This changes scheduling only, not how
each worker computes its candidate list.

Physical-card Static nested captures are separate regressions in
`StaticNestedRecoveryTests.cs`. The offset selector takes the **known seed
key's type**, as the GUI's `validKeyType` caller does, not the target type.
The `knownType` field in the static fixtures has this meaning. Passing the
unknown B type for an A-seeded capture reproduces the old port bug; it does
not demonstrate a fault in the GUI's C solver.

`static_encrypted_vectors.c` includes the original `staticnested_1nt` and
`staticnested_2x1nt_rf08s` sources and prints a candidate-set fingerprint plus
RF08S seed vectors for `StaticEncryptedNestedRecoveryTests`. Build it with
`crapto1.c`, `crypto1.c`, `bucketsort.c`, and `parity.c` from `software/src`.
The fingerprint sorts keys numerically, then applies FNV-1a 64 to each key's
six bytes in little-endian order. No device is required.
