# Managed hardnested benchmark

Results and short methodology: [BASELINE.md](BASELINE.md).

Pre-optimization GUI comparison: [ChameleonUltraGUI 1.4](GUI-1.4.md)
(10 datasets, 60 verified recoveries; measured 2026-11-10).

Latest optimized implementation: [GUI-1.4-OPTIMIZED.md](GUI-1.4-OPTIMIZED.md).

The console harness calls the app's actual `ManagedHardnestedEngine`, with no
native solver dependency. It creates a deterministic capture (the same xorshift
generator as the C reference fixture), recovers without supplying the target
key, verifies the result and reports elapsed time and peak working set.

```sh
dotnet run --project software/benchmarks/hardnested -c Release -- 12000 112233445566
dotnet run --project software/benchmarks/hardnested -c Release -- 4096 FFFFFFFFFFFF
```

Generation arguments: `COUNT KEY_HEX [OUTPUT [UID_HEX [SEED_HEX [--generate-only]]]]`.
Defaults: UID `12345678`, xorshift32 seed `9325ABCD`. To recover an existing capture:
`HardnestedBench --capture FILE UID_HEX EXPECTED_KEY_HEX`.
The expected key is used only to check the result, never passed to the solver.

## Comparison with current Proxmark3

Use the pinned upstream revision documented in
`software/chamelab/ChameLab.Core/Crypto/Hardnested/NOTICE.md`:

```sh
git clone https://github.com/RfidResearchGroup/proxmark3.git /path/to/reference
git -C /path/to/reference checkout 1b71dfbbd4c156c34c02cfdd9140470d97d1336c
dotnet run --project software/chamelab/ChameLab.Tools -- prepare-hardnested-reference /path/to/reference
make -C /path/to/reference -j8 client SKIPQT=1 SKIPGD=1
dotnet publish software/benchmarks/hardnested -c Release -r linux-x64 \
  --self-contained -o /path/to/managed
dotnet run --project software/chamelab/ChameLab.Tools -- compare-hardnested \
  /path/to/managed/HardnestedBench /path/to/reference/client/proxmark3 \
  /path/to/results 3
```

This revision of the upstream CLI rejects `-r` without connected hardware and
overwrites `-f`. `prepare-hardnested-reference` fixes only those two command-routing
conditions in a disposable checkout. The C hardnested implementation, compiler
optimizations, SIMD detection and threading are unchanged. All original tables
are available to it. No known target key is passed to either recovery engine.

`compare-hardnested` runs all eight deterministic sets in [CASES.json](CASES.json): five
keys, five UIDs, five seeds and four capture sizes. Use `--only reference zero`
to select cases. It checks the reference set against the independent C fixture
and requires all 256 first-byte values in every capture.

Captures are generated before timing. Each solver reads the same nonce/parity
records from its supported format. Three fresh-process runs per solver/case run
sequentially, alternating solver order. `Stopwatch` measures monotonic total wall time; `/usr/bin/time` measures peak RSS, including input, startup, tables and the native startup benchmark.
Report medians; check every recovered key and exit code. Older stored baselines used GNU time wall time; new runs also retain that value as `gnu_seconds`. File caches are not
flushed. Keep other builds/benchmarks idle. Logs, capture SHA-256 hashes and raw
measurements are saved in the output directory; [RESULTS.json](RESULTS.json)
contains the published measurements.

Reference environment: i9-13900H, 20 logical CPUs, Ubuntu 24.04/WSL2
`6.18.54.1-microsoft-standard-WSL2`; .NET SDK 10.0.402/runtime 10.0.13, Release,
AVX2, up to 16 managed workers; GCC 13.3.0, AVX2, 20 native threads. Both builds
and tables reside on the Linux filesystem. Shared managed assembly SHA-256:
`4ec1630974a8d75ebf50654ea562b6c5c9800e4dbd751ae6339b93fb3c62e962`.
These measure end-to-end recovery on synthetic data on this host; they do not
establish kernel throughput or speed on every capture/platform.

## ChameleonUltraGUI

Use the source checkout pinned in the comparison report. The C# tool builds the
unmodified Release library and writes a small C adapter outside the checkout.
The adapter checks both native success status and expected key, including zero.
Run under Linux/WSL from this repository, with GCC and CMake installed:

```sh
dotnet run --project software/chamelab/ChameLab.Tools -- prepare-hardnested-gui \
  /path/to/ChameleonUltraGUI /path/out/gui-reference
dotnet run --project software/chamelab/ChameLab.Tools -- compare-hardnested \
  /path/to/managed/HardnestedBench /path/out/gui-reference /path/to/results 3 \
  --gui-root /path/to/ChameleonUltraGUI
```

This adds the upstream capture and filtering fixtures to the eight synthetic
sets. `--only` also accepts `gui-capture` and `gui-filtering`. All inputs are
prepared before timing; native GUI input bytes and headers remain unchanged.
The driver warms only its own process machinery before timing fresh solver
processes. No expected key enters either solver.

## Browser worker

Publish the browser project with Release AOT, install Playwright and Chromium,
then run:

```sh
node software/chamelab/ChameLab.Browser/tests/browser-worker.cjs \
  /path/to/publish/wwwroot \
  software/chamelab/ChameLab.Tests/Reference/hardnested_capture.txt
```

An optional third argument selects a Chrome executable. The test serves only on
loopback, checks malformed input, cancellation during reduction, restart and
known-key recovery, and verifies that the UI event loop advances. It runs without
cross-origin isolation. CI runs it against the published app.

## Correctness and portability

```sh
dotnet test software/chamelab/ChameLab.Tests -c Release --filter FullyQualifiedName~Hardnested
```

Tests cover the independent C fixture, zero/nonzero keys and UIDs, partial sums
against direct upstream enumeration, scalar/SIMD lanes and tail blocks, invalid
input, cancellation and reuse. CI includes Windows, Linux and macOS; existing
Android and iOS builds consume the same assembly. To exercise the runtime's full
scalar fallback on desktop, run the harness with `DOTNET_EnableHWIntrinsic=0`.

Earlier checks on this solver assembly (2026-11-04): 443 Windows Release tests
passed; Desktop, Android arm64 (optional AOT disabled) and Browser AOT builds
succeeded. Chromium passed worker recovery, cancellation, restart and UI
responsiveness checks; Linux scalar recovery passed. macOS/iOS and physical
cards/mobile devices were not tested locally.
