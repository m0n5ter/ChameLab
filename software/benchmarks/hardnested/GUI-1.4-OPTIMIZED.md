# Optimized managed hardnested vs GUI 1.4 - 2026-11-10

**C# wins 10/10 median comparisons; 124/124 recoveries correct.** Speedup: **1.03-5.54x** on this host.

| Dataset | Nonces | C# (s) | GUI C (s) | Speedup | Peak MiB (C# / GUI) |
| --- | ---: | ---: | ---: | ---: | ---: |
| reference | 12,000 | 0.567 | 0.630 | 1.11x | 183 / 172 |
| sparse | 4,096 | 0.537 | 0.582 | 1.08x | 196 / 215 |
| ones-sparse | 4,096 | 21.342 | 65.178 | 3.05x | 354 / 147 |
| zero | 12,000 | 0.381 | 0.399 | 1.05x | 163 / 141 |
| transport | 12,000 | 0.525 | 0.552 | 1.05x | 170 / 145 |
| mixed | 12,000 | 0.688 | 0.706 | 1.03x | 199 / 174 |
| new-seed | 8,192 | 0.529 | 0.607 | 1.15x | 193 / 173 |
| ones-dense | 24,000 | 8.979 | 49.701 | 5.54x | 323 / 144 |
| gui-capture | 1,320 | 0.836 | 0.868 | 1.04x | 224 / 160 |
| gui-filtering | 1,772 | 0.564 | 0.584 | 1.04x | 168 / 214 |

**Method:** seven fresh-process runs per engine for eight short sets; three for each all-ones set. Sequential pairs alternate solver order. Monotonic `Stopwatch` wall time includes input, startup, tables and recovery; GNU time supplies peak RSS. Caches are not flushed; no builds or other benchmarks run concurrently. Every key and exit status is checked, including explicit native key-found output for zero. Corpus hashes match the [before-optimization baseline](GUI-1.4.md).

**Builds:** i9-13900H, 20 logical CPUs, Ubuntu/WSL2; .NET 10.0.13/SDK 10.0.402, self-contained Linux x64 Release, AVX2, up to 16 workers. Unmodified GUI 1.4 (`dfd9f1e9b5a748c3f8eab9b21663af1ca926db33`), GCC 13.3 Release, default 128-bit SIMD and 20 search threads. Both were measured again with the C# [comparison driver](../../chamelab/ChameLab.Tools/BenchmarkTools.cs).

**Changes:** batched, balanced SIMD intersections; duplicate filter-page elimination; seeded parallel domains; parallel candidate ranges; state-derived sum priors; a 1 MiB packed partial-sum cache. The solver remains managed C#, with scalar fallback. Peak memory is higher in some cases, especially all-ones inputs; short-case time ranges overlap, so these medians do not imply a speedup on every capture or CPU.

**Validation:** 563 Windows Release tests passed; all 44 hardnested tests also passed with hardware intrinsics disabled. Tests compare every effective bitmap word with the original reduction, all cached partial sums, invariant checks and range boundaries. Browser Release AOT/Chromium recovery, cancellation, restart and UI responsiveness passed (4.64 s recovery, no cross-origin isolation). Mobile/ARM and macOS were not measured locally.

Managed Core SHA-256: `6219fe437eb00e3c2cb8b50baa7b8daedfaeda63f72370397b9e0cd6b50b1466`. GUI library SHA-256: `3d3b6d4606e8bc7a8099c9b7bdf11f7c5f6f3a4c73c57a7207dece1eefffcc5f`.

[Raw runs](GUI-1.4-OPTIMIZED-RESULTS.json) | [Reproduce](README.md#chameleonultragui). Use 7 repeats with the eight short `--only` IDs and 3 repeats with `--only ones-sparse ones-dense`. Full logs/captures remain in WSL `/home/monster/gui-confirmed-short-7` and `/home/monster/gui-confirmed-long-3`.
