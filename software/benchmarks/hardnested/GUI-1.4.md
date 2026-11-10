# Before optimization: ChameLab vs ChameleonUltraGUI 1.4 - 2026-11-10

**60/60 recoveries passed. GUI wins 8/10 cases (4.84-14.52x); ChameLab wins both all-ones cases (1.80-2.75x).**

| Dataset | Nonces | ChameLab C# (s) | GUI C (s) | Faster | Peak MiB (C# / GUI) |
| --- | ---: | ---: | ---: | --- | ---: |
| reference | 12,000 | 5.86 | 0.66 | GUI 8.88x | 113 / 173 |
| sparse | 4,096 | 4.49 | 0.72 | GUI 6.24x | 101 / 213 |
| ones-sparse | 4,096 | 28.19 | 77.47 | C# 2.75x | 239 / 147 |
| zero | 12,000 | 2.33 | 0.45 | GUI 5.18x | 96 / 141 |
| transport | 12,000 | 5.11 | 0.65 | GUI 7.86x | 102 / 144 |
| mixed | 12,000 | 6.23 | 0.83 | GUI 7.51x | 118 / 166 |
| new-seed | 8,192 | 5.63 | 0.69 | GUI 8.16x | 113 / 173 |
| ones-dense | 24,000 | 34.83 | 62.82 | C# 1.80x | 190 / 144 |
| gui-capture | 1,320 | 15.83 | 1.09 | GUI 14.52x | 151 / 160 |
| gui-filtering | 1,772 | 3.82 | 0.79 | GUI 4.84x | 97 / 218 |

**Method.** Three fresh-process runs per engine/case, sequential, alternating order; medians of GNU time wall time and peak RSS. Identical nonce/parity records and UID, prepared before timing. Includes input, runtime/library startup, tables and recovery; excludes Flutter/Avalonia startup. Caches unflushed. Expected keys only validate results; the native wrapper also checks the solver success status (including zero key).

**Builds.** i9-13900H, 20 logical CPUs, Ubuntu/WSL2 `6.18.54.1-microsoft-standard-WSL2`. ChameLab `6562bc99d38e68013cd06c9cb1ab52bf5429ea53`, .NET SDK 10.0.402/runtime 10.0.13, self-contained linux-x64 Release, AVX2, up to 16 workers. GUI [release 1.4](https://github.com/GameTec-live/ChameleonUltraGUI/releases/tag/1.4), commit `dfd9f1e9b5a748c3f8eab9b21663af1ca926db33`, unmodified C sources, GCC 13.3.0/CMake 3.28.3 Release (`-O3 -fno-semantic-interposition`), default 128-bit SIMD, 20 search threads.

**Inputs.** Original eight sets from [CASES.json](CASES.json), with binary hashes matching the previous corpus. GUI-capture: longest hex payload in upstream `test/recovery_test.dart`; GUI-filtering: upstream `test/native/fixtures/hardnested-filtering.bin`. Both are from the pinned release. Binary format: big-endian UID/block/key-type header (6 bytes), then paired nonce/nonce/packed parity records (9 bytes). Text format preserves record order as decimal `nonce|parity`. Every input covers all 256 first-byte values.

**Code comparison.** [PR #1088](https://github.com/GameTec-live/ChameleonUltraGUI/pull/1088) shares immutable pages and packs tables; its source explicitly credits ChameLab for partial-sum enumeration. It additionally uses four reduction workers, selectivity-sorted filters, full/empty-page shortcuts and batched intersections. Our reduction traverses the 512 maps sequentially with active-word filtering. The first C# reference log spends 3.90 s in reduction (5.14 s total), while the entire paired GUI process takes 0.58 s. Reduction is a concrete profiling priority; this does not establish the cause of every timing difference.

**Reproduce.** Build the pinned release and [reference adapter generator](../../chamelab/ChameLab.Tools/BenchmarkTools.cs), which calls the same `mfnestedhard` arguments as the GUI FFI entry point and checks its result. Publish our existing [harness](README.md). For each input, run the following pair three times, alternating order:

```sh
# Run under Linux/WSL, inside this checkout; use the recorded ChameLab binary.
dotnet run --project software/chamelab/ChameLab.Tools -- prepare-hardnested-gui GUI_CHECKOUT /path/out/gui-reference
dotnet run --project software/chamelab/ChameLab.Tools -- compare-hardnested \
  MANAGED_EXE /path/out/gui-reference /path/results 3 --gui-root GUI_CHECKOUT
```

SHA-256: GUI library `3d3b6d4606e8bc7a8099c9b7bdf11f7c5f6f3a4c73c57a7207dece1eefffcc5f`; managed Core assembly `d64196a6f9e973dd1748c34aa962dea21bb33e9367e1992bfe05c632af641042`.

[Raw runs and capture hashes](GUI-1.4-RESULTS.json). Full logs and converted captures remain in WSL `/home/monster/gui-comparison`. Results apply to these inputs on this host; mobile/ARM performance was not measured.
