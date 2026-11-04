# Managed hardnested vs Proxmark3 — 2026-11-04

Full recovery, three fresh-process runs per case, executed sequentially after
builds finished. Every run recovered the expected key. Times below are medians
of `/usr/bin/time` wall time; memory is median peak RSS. Raw measurements are in
[RESULTS.json](RESULTS.json); reproduction commands are in [README.md](README.md).

| Nonces | Key | Managed C# | Proxmark3 C | Speedup | Managed MiB | Native MiB |
| ---: | --- | ---: | ---: | ---: | ---: | ---: |
| 12,000 | `112233445566` | 5.38 s | 10.10 s | **1.88×** | 114 | 1,738 |
| 4,096 | `112233445566` | 4.17 s | 9.48 s | **2.27×** | 102 | 1,781 |
| 4,096 | `FFFFFFFFFFFF` | 25.28 s | 67.63 s | **2.68×** | 236 | 1,716 |

Managed peak RSS over all individual runs: 101.9–249.6 MiB. Native: 1,715–1,785
MiB. Median memory use is approximately 7–17 times lower. These captures are
synthetic and deterministic; they do not establish a speedup on every capture,
CPU or operating system, or a universal memory ceiling.

## Environment and fairness

- Intel Core i9-13900H, 20 logical CPUs exposed to Ubuntu under WSL2; Linux
  `6.18.54.1-microsoft-standard-WSL2`.
- Managed: .NET SDK 10.0.402, self-contained Linux x64 Release build, runtime
  10.0.13. The benchmark references the actual shared app assembly. SIMD uses
  `Vector<ulong>` (AVX2); parallel work is capped at 16 workers.
- Native: upstream HEAD `1b71dfbbd4c156c34c02cfdd9140470d97d1336c`, reconfirmed
  on 2026-11-04, GCC 13.3.0, `make -j8 client SKIPQT=1 SKIPGD=1`, automatically
  selected AVX2 and 20 threads. All 351 original LZ4 tables are installed.
- Only two CLI conditions were patched to permit offline file recovery and
  preserve the explicitly supplied filename. The native hardnested solver,
  initialization, probability selection and SIMD kernels were not modified.
  The exact patch is produced by `prepare_reference.py`.
- Both executables and the native table files reside on the Linux filesystem.
  Both consume identical nonces and parity, UID `12345678`; neither solver
  receives the expected key. The managed harness checks its result; the comparison
  requires an explicit native key-found result and successful process exit.
- Timing includes process startup, table loading and recovery, including the
  native solver's built-in startup throughput benchmark. Managed fixture creation
  and export are also included. This measures the complete operation rather
  than only the brute-force kernel. OS file caches are not deliberately flushed.
- Managed assembly SHA-256:
  `4ec1630974a8d75ebf50654ea562b6c5c9800e4dbd751ae6339b93fb3c62e962`.

## Validation performed

- Windows Release suite: **443 passed**, zero failures, including all 18 new
  managed hardnested tests. The suite also includes concurrent localization work.
- Desktop Release and Android arm64 Release builds: zero warnings/errors.
  The final Android build disabled optional AOT; physical-device recovery was
  not exercised here.
- Browser Release AOT publish succeeded. Chrome 156.0.8078.80 ran malformed-input,
  cancellation during reduction, restart and full C-fixture recovery checks.
  The final recovery took 12.08 s in this browser check; the UI timer continued
  and `crossOriginIsolated` was false. This is not a browser/native speed comparison.
- Linux full recovery with `DOTNET_EnableHWIntrinsic=0` recovered the expected
  key from 12,000 nonces, exercising the scalar fallback.
- macOS/ARM64 and iOS were not run locally. CI has Windows/Linux/macOS recovery
  tests, browser worker checks and the existing iOS device/simulator builds.
  No claim of physical-card or physical-mobile-device validation is made.
