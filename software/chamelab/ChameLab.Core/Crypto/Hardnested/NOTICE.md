# Managed hardnested

Derived from [RfidResearchGroup/proxmark3](https://github.com/RfidResearchGroup/proxmark3/tree/1b71dfbbd4c156c34c02cfdd9140470d97d1336c),
commit **1b71dfbbd4c156c34c02cfdd9140470d97d1336c**, checked against upstream HEAD
on 2026-11-03. Sources: `client/src/cmdhfmfhard.c`,
`client/deps/hardnested/hardnested_bf_core.c`, `hardnested_bruteforce.c`,
`hardnested_bitarray_core.c`, and `client/resources/hardnested_tables/`.

Copyright (C) Proxmark3 contributors. Hardnested core copyright (C) 2016, 2017
by piwi, originally GPL-2.0-or-later. This managed adaptation is distributed
under GPL-3.0-or-later, as permitted by those licenses. See the repository LICENSE.

The algorithm is described by Carlo Meijer and Roel Verdult in
"Ciphertext-only Cryptanalysis on Hardened Mifare Classic Cards", CCS 2015.
The bitsliced implementation builds on Aram Verstegen's crypto1_bs code,
with this notice retained from upstream:

Copyright (c) 2015-2016 Aram Verstegen

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

## Port and representation

- Uses the upstream first/second-byte bitflip constraints, partial sums,
  cross-byte state invariants, encrypted-feedback cipher, and bitsliced filter
  circuits. All 19 sum guesses remain eligible when consistent with observations.
- Generates partial sums by sharing binary-tree subproblems, instead of repeating
  the same filter calls for every path. A 1 MiB packed resource decodes into two
  1 MiB arrays at startup; it replaces the original sum bitmaps.
- `tables.bin` contains the same 241 effective (half, bitflip) tables selected by upstream's
  0.9901 threshold. It shares equal 256-byte pages; it requires no decompressor,
  native library, download, runtime code generation, or temporary file.
  `tables.manifest.json` records hashes for all 351 original LZ4 files, decoded
  files, and the packed resource. Integer storage is explicitly little endian.
- Batched SIMD page reduction, independent worker domains, shared immutable
  result pages and early common high-bit elimination avoid the upstream 1 GiB
  of per-byte state bitmaps. Scalar page operations remain available.
- Candidate lists are partitioned once by both partial sums. Cross-byte matching
  is lazy and cached per partition; sum guesses use hypergeometric likelihood
  and a prior derived from the surviving state domain. Expected search cost
  selects the first byte. Parallel matching and search use bounded work units.
- `Vector<ulong>` supplies portable SIMD, with a separate scalar `ulong` circuit
  when SIMD is unavailable. Both are generated from `tools/hardnested/core.cs.txt`.
  A surviving state is rolled back and checked against **every** original sample.
- Mutable state belongs to a recovery; only immutable tables are shared.
  Cooperative cancellation applies to desktop/mobile work; the browser terminates
  its isolated .NET worker. There is no dependence on a particular endianness,
  pointer size, OS, native library, executable, unsafe block or P/Invoke.

From the repository root, regenerate tables using the pinned upstream checkout:
`dotnet run --project software/chamelab/ChameLab.Tools -- import-hardnested-tables PATH_TO_PROXMARK3`.
The importer verifies every compressed and decoded input against the manifest,
and requires the final packed SHA-256 to match before replacing the resource.
Regenerate circuits with
`dotnet run --project software/chamelab/ChameLab.Tools -- generate-hardnested`.
Neither tool is needed to build or run the application.

See `software/benchmarks/hardnested` for repeatable performance comparisons and
`ChameLab.Tests/Crypto/ManagedHardnestedTests.cs` for regression coverage.

The 2026-11 optimization also takes inspiration from ChameleonUltraGUI 1.4,
commit `dfd9f1e9b5a748c3f8eab9b21663af1ca926db33` (GPL): independent reduction
domains, shared immutable pages and batched page intersections. The managed
implementation merges the domains before candidate generation; tests compare
every effective bitmap word against the original sequential algorithm.

`partial-sums.bin` stores both deterministic partial sums as two nibbles per
byte (1 MiB). Regenerate with `generate-hardnested-partial-sums`; the ordinary
`generate-hardnested --check` verifies it too. SHA-256:
`ba9893dffba029dd4fa014fe152d4ca7fa8ada2ed59b5c05f7ac3fe5e7dec0a6`.
Tests compare every decoded entry with the original tree enumeration. Sum
priors and expected search cost now select the first byte and guess order.
