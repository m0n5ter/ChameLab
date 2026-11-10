# Nested, static encrypted nested and Darkside vs ChameleonUltraGUI

The application remains managed C#. The benchmark calls its real Core assembly
and the GUI's unmodified `librecovery.so` through the same .NET harness.
No physical card or device is needed.

Reference: GUI 1.4, commit
[`dfd9f1e9`](https://github.com/GameTec-live/ChameleonUltraGUI/tree/dfd9f1e9b5a748c3f8eab9b21663af1ca926db33/chameleonultragui/src).
On 2026-11-10, fetched upstream main was
[`6799e086`](https://github.com/GameTec-live/ChameleonUltraGUI/commit/6799e086d1a2bf2ef4049e1d6313b55c92258746);
`git diff` found no changes anywhere in `chameleonultragui/src` between these
commits. The reference checkout was clean.

## Measured results - 2026-11-10

i9-13900H, 20 logical CPUs, Ubuntu 24.04.1/WSL2, .NET 10.0.13, self-contained Linux x64 Release published on Windows using .NET SDK 10.0.402; GUI GCC 13.3 Release. These compare application recovery paths with their normal parallelism, rather than equal-thread language implementations.

All 12 optimized warm medians are lower than GUI. The static encrypted sample ranges overlap in three of four cases, so its small median advantage is not a guarantee for every call. All 324 warmup/measured calls passed validation.

| Case | Before ms | After ms | GUI ms | Before / after | GUI / after |
| --- | ---: | ---: | ---: | ---: | ---: |
| nested-000000000000 | 520.16 | 344.26 | 722.05 | 1.51x | 2.10x |
| nested-FFFFFFFFFFFF | 410.67 | 297.52 | 698.11 | 1.38x | 2.35x |
| nested-A0A1A2A3A4A5 | 384.03 | 276.39 | 632.22 | 1.39x | 2.29x |
| nested-112233445566 | 459.65 | 322.96 | 667.41 | 1.42x | 2.07x |
| darkside-FFFFFFFFFFFF | 114.24 | 5.40 | 62.70 | 21.15x | 11.61x |
| darkside-A0A1A2A3A4A5 | 261.33 | 15.31 | 107.40 | 17.07x | 7.02x |
| darkside-112233445566 | 170.01 | 10.07 | 80.05 | 16.88x | 7.95x |
| darkside-zero-A0A1A2A3A4A5 | 726.18 | 100.51 | 289.76 | 7.23x | 2.88x |
| static-encrypted-000000000000 | 304.86 | 82.08 | 89.85 | 3.71x | 1.09x |
| static-encrypted-FFFFFFFFFFFF | 246.07 | 80.30 | 93.42 | 3.06x | 1.16x |
| static-encrypted-112233445566 | 246.09 | 64.87 | 78.69 | 3.79x | 1.21x |
| static-encrypted-A0A1A2A3A4A5 | 258.07 | 74.61 | 80.74 | 3.46x | 1.08x |

The complete pre/post ChameLab candidate multiset matches in every case. GUI unique sets match in 11/12 cases: zero-key Nested retains 21 repeated candidates in ChameLab and only its maximum-frequency key in GUI; both recover the real key. For zero-key static encrypted, GUI emits 78,699 entries including 30 duplicates; both unique sets contain the same 78,669 keys.

### First call and memory

First calls are single observations including JIT and lazy tables, not cold medians. The speedups above apply after warmup. Initial table construction can make a first call slower than the old implementation; static encrypted first calls remain substantially slower than GUI. Extra shared lookup data can reach approximately 72 MiB when all initial seed lists and Darkside planes have been used. Peak process memory is also higher, especially when Nested uses many workers.

| Case | First call ms: before / after / GUI | Peak MiB: before / after / GUI |
| --- | ---: | ---: |
| nested-000000000000 | 626.8 / 825.0 / 783.1 | 364 / 357 / 53 |
| nested-FFFFFFFFFFFF | 616.7 / 558.7 / 741.4 | 347 / 367 / 50 |
| nested-A0A1A2A3A4A5 | 573.7 / 643.2 / 606.5 | 267 / 306 / 50 |
| nested-112233445566 | 673.0 / 629.9 / 638.6 | 304 / 354 / 50 |
| darkside-FFFFFFFFFFFF | 124.3 / 184.6 / 62.6 | 37 / 49 / 41 |
| darkside-A0A1A2A3A4A5 | 265.3 / 212.7 / 128.4 | 37 / 49 / 41 |
| darkside-112233445566 | 202.0 / 215.1 / 103.9 | 37 / 49 / 39 |
| darkside-zero-A0A1A2A3A4A5 | 706.1 / 343.6 / 298.3 | 67 / 157 / 94 |
| static-encrypted-000000000000 | 315.8 / 330.8 / 94.2 | 96 / 128 / 52 |
| static-encrypted-FFFFFFFFFFFF | 301.8 / 307.9 / 88.5 | 87 / 106 / 50 |
| static-encrypted-112233445566 | 308.2 / 308.0 / 107.5 | 84 / 98 / 50 |
| static-encrypted-A0A1A2A3A4A5 | 281.0 / 318.2 / 95.4 | 86 / 100 / 50 |

[Raw samples, allocations, counts, fingerprints and binary hashes](RESULTS-2026-11-10.json).

## Reproduce

Use Linux/WSL for the GUI comparison. Save a self-contained baseline before
changing Core, then publish the optimized version to a different directory:

```powershell
dotnet publish software/benchmarks/recovery -c Release -r linux-x64 --self-contained true -o BEFORE
# Apply the optimization, then:
dotnet publish software/benchmarks/recovery -c Release -r linux-x64 --self-contained true -o AFTER
```

Build the unmodified GUI using its CMake Release configuration:

```text
git clone https://github.com/GameTec-live/ChameleonUltraGUI.git gui
git -C gui checkout dfd9f1e9b5a748c3f8eab9b21663af1ca926db33
cmake -S gui/chameleonultragui/src -B gui-build -DCMAKE_BUILD_TYPE=Release
cmake --build gui-build -j8
AFTER/RecoveryBench --compare software/chamelab/ChameLab.Tests/Reference/crypto_vectors.json \
  BEFORE/RecoveryBench AFTER/RecoveryBench gui-build/librecovery.so OUTPUT_DIRECTORY 7 2
```

The baseline in this report is ChameLab `1e236a37` before the changes in this
working tree. The harness can be copied into that checkout to reproduce it.
`--compare` is needed only in AFTER; the worker arguments are unchanged.

## Method and validation

Each fixture/engine runs in a fresh process with two untimed warmups and seven
measured calls. Engines run sequentially; their order rotates between fixtures.
Builds and other benchmarks must finish before measurement. Tiered compilation
and ReadyToRun are disabled. No forced GC is used. Timings include recovery,
buffer clearing, result allocation and, for GUI, P/Invoke and copying/freeing the
returned array. Build, process startup, input loading, validation and printing
are excluded. `coldMs` records the first call separately, including lazy tables
and JIT work; it is a single observation, not a cold-start median.

Every call must recover its known key, or return empty for the independent
negative Darkside fixture. Complete sorted candidate multisets must remain
stable and must match the pre-optimization solver. Complete unique sets also
must match GUI for static encrypted nested and Darkside. Nested reports set
equality separately: GUI selects only keys with the maximum frequency; ChameLab
retains up to 50 keys occurring at least twice, ordered by frequency and key.
Those different policies are retained.

Cross-engine comparisons use candidate counts and FNV-1a 64 fingerprints of all
sorted keys. Within-engine stability is checked by direct array comparison;
regression tests also directly compare complete recovered state/candidate arrays.

All four independent Nested captures, two positive Darkside captures, one
negative Darkside capture and the two-round parity-zero capture come from the
saved C reference vectors. Four static encrypted captures use known zero,
all-ones, transport and mixed keys. Each static capture is synthesized with
Crypto1 and checked against the independent native solver; the existing test
suite also checks the original C example's full fingerprint.

Peak MiB is the whole harness process's peak resident memory, including .NET in
all three modes. Native allocations are excluded from `allocatedBytes` and
must not be inferred from managed allocation counts. GUI's Darkside wrapper
retains some allocations; process isolation bounds this to nine calls per
fixture. It is not a leak-free steady-state memory benchmark.

## Implementation

Recovery32 lazily shares the input-independent initial five-filter-bit candidate
lists across sectors and workers (approximately 64 MiB if all 32 are used).
It uses comparison sorting for short contribution lists and linear grouping
for larger ones. Public ordered Crypto1 producers keep their previous path.
Nested keeps its existing independent-nonce scheduling and candidate ranking.

Static encrypted nested computes the first feedback rounds on two threads,
then dynamically distributes independent groups among up to eight workers.
Each worker owns small growable buffers and a candidate set; merges happen
after joining. All buffers are cleared before return to the pool. RF08S seed
filtering uses two byte-substitution tables and avoids allocating a 64 KiB
membership table when the other side contains one key. Zero-nonce behavior,
filter order and duplicates are preserved.

Darkside uses 8 MiB of shared, permuted paired-filter bit planes to examine
64 prefixes per word, checks response parity after each
reconstructed byte, and streams directly to key rollback. Parity-zero recovery
replaces 35 plaintext rollback clocks with six small linear-transform lookups;
the encrypted clocks still execute exactly. Prefix halves run independently,
and candidate rows use up to eight workers, then merge in original row order.
Intersection removes matched entries from one set to preserve deduplication.

Browsers run sequentially and reuse tables in the persistent recovery worker.
The caches trade additional shared memory for repeated-call performance;
first use includes building each newly encountered initial list.

## Checks

```text
dotnet test software/chamelab/ChameLab.Tests -c Release
dotnet run --project software/chamelab/ChameLab.Tools -c Release -- check-languages
dotnet publish software/chamelab/ChameLab.Browser -c Release
node software/chamelab/ChameLab.Browser/tests/key-recovery-worker.cjs \
  PUBLISH/wwwroot software/chamelab/ChameLab.Tests/Reference/crypto_vectors.json CHROME_EXECUTABLE
```

The browser test requires Playwright and Chromium/Chrome. It checks all three
attacks, the complete static encrypted C fingerprint, repeated recovery,
cancellation, restart and a responsive page timer without cross-origin isolation.
Regression tests additionally compare all 32 cached seed multisets against
independent extension, complete parallel/ordered state multisets including zero
and duplicates, and 4,096 successful Darkside parity paths with every relevant
parity bit flipped at every trial. Existing exhaustive RF08S seed checks remain.

Recorded validation: **621/621 Windows Release tests passed**; the 12 new
optimization cases also passed with `DOTNET_EnableHWIntrinsic=0`.
The current Release AOT browser worker passed in Chrome: Nested returned both
expected candidates, both Darkside variants returned the expected key, and two
successive static encrypted recoveries each matched all 44,075 C-reference
candidates. Cancellation, worker restart and page responsiveness passed with
`crossOriginIsolated=false`. ARM/mobile and macOS performance were not measured.
