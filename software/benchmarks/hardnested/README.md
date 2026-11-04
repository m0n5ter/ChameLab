# Managed hardnested benchmark

Results and short methodology: [BASELINE.md](BASELINE.md).

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
`software/chamelab/ChameLab/Crypto/Hardnested/NOTICE.md`:

```sh
git clone https://github.com/RfidResearchGroup/proxmark3.git /path/to/reference
git -C /path/to/reference checkout 1b71dfbbd4c156c34c02cfdd9140470d97d1336c
python3 software/benchmarks/hardnested/prepare_reference.py /path/to/reference
make -C /path/to/reference -j8 client SKIPQT=1 SKIPGD=1
dotnet publish software/benchmarks/hardnested -c Release -r linux-x64 \
  --self-contained -o /path/to/managed
python3 software/benchmarks/hardnested/compare.py \
  /path/to/managed/HardnestedBench /path/to/reference/client/proxmark3 \
  /path/to/results 3
```

This revision of the upstream CLI rejects `-r` without connected hardware and
overwrites `-f`. `prepare_reference.py` fixes only those two command-routing
conditions in a disposable checkout. The C hardnested implementation, compiler
optimizations, SIMD detection and threading are unchanged. All original tables
are available to it. No known target key is passed to either recovery engine.

`compare.py` runs all eight deterministic sets in [CASES.json](CASES.json): five
keys, five UIDs, five seeds and four capture sizes. Use `--only reference zero`
to select cases. It checks the reference set against the independent C fixture
and requires all 256 first-byte values in every capture.

Captures are generated before timing. Each solver reads the same nonce/parity
records from its supported format. Three fresh-process runs per solver/case run
sequentially, alternating solver order. `/usr/bin/time` measures total wall time
and peak RSS, including input, startup, tables and the native startup benchmark.
Report medians; check every recovered key and exit code. File caches are not
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

## Browser worker

Publish the browser project with Release AOT, install Playwright and Chromium,
then run:

```sh
node software/benchmarks/hardnested/browser-worker.cjs \
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
