# Nested benchmark after managed optimization — 2026-11-01

Measured on Intel Core i9-13900H (14 cores / 20 logical CPUs), Windows x64
10.0.26100. C: MSVC toolset 14.51.36231, C11 `/O2`.
C#: .NET SDK 10.0.402, runtime 10.0.14, Release, optimizing JIT with tiering
and ReadyToRun disabled. The managed code includes the current working-tree
optimization on top of commit `f7a6cd533d3ef61fe2828822397fb5a9b784788b`.
The native algorithm sources are unchanged.

Median elapsed milliseconds per full recovery; two warmups and seven measured
calls per cell. Tests were not running during these measurements.

| Known key | C, 1 thread | C#, 1 worker | C, up to 4 threads | C#, up to 4 workers |
| --- | ---: | ---: | ---: | ---: |
| `000000000000` | 2121.80 | 2109.85 | 896.42 | 800.36 |
| `FFFFFFFFFFFF` | 2047.07 | 2013.22 | 818.98 | 759.62 |
| `A0A1A2A3A4A5` | 1552.13 | 1526.44 | 952.76 | 553.49 |
| `112233445566` | 1896.74 | 1904.27 | 728.08 | 633.03 |
| Sum of per-case medians | 7617.74 | 7553.77 | 3396.24 | 2746.49 |

Ratios of the sums of the per-case medians:

- One worker: C# takes **0.992×** the C time (effectively the same level; under 1% difference).
- Up to four workers: C# is **1.24× faster** than C (a 19.1% time reduction).
- Compared with the previous sequential managed implementation, the new default
  is approximately **4.70× faster** on this workload. This before/after figure
  includes both algorithm changes and parallelism and comes from separate runs.

The default application path is the managed four-worker implementation, capped
by CPU and nonce-candidate counts. Browser builds use one worker. Production
does not depend on the native benchmark DLL.

Changes:

- Group Recovery32 states by their high contribution byte instead of fully
  sorting each recursive range. The public Crypto1 producers keep their
  characterized ordering; only Nested uses the unordered producer.
- Cache filter and candidate-array references outside the inner loops.
- Stream states directly into rollback, reusing a workspace and scratch buffer
  per worker across nonce candidates.
- Sort a flat key array and count repetitions instead of maintaining a large
  dictionary. Merge per-worker keys with deterministic frequency/key ordering.
- Process nonce candidates with up to four independent workers and round-robin
  assignment, with cancellation checks and buffer cleanup.

All **144 recoveries** returned the expected key. Every invocation's complete
candidate set matched the independent C reference and remained stable (21, 3,
1 and 2 keys respectively; order ignored). Tests additionally compare the
ordered sequential/parallel candidate lists and compare all states,
including duplicates, between grouped and sorted recovery on zero, maximal
and mixed inputs. Multiplicities of zero-contribution states are retained;
empty sentinel states are discarded by Nested.

Validation: the complete Release test suite passed, **367 tests**, including
the new grouped-recovery regressions. Benchmark Release compilation succeeded
with zero warnings and errors.

This is a comparison of these implementations and four synthetic fixtures on
this machine. The small sequential difference should not be interpreted as a
general language advantage. No core affinity or machine isolation was used.
MSVC does not enable the GCC-only C filter lookup table. Native limitations
and the full methodology are described in [README.md](README.md).

[Raw samples](four-worker.json) · [Previous report](BASELINE.md) · [Previous samples](baseline.json)

This report records the previous four-worker implementation. The runner now
uses the newer table rollback, radix sort and automatic CPU-count limit.
Current command from the repository root:

```powershell
./software/benchmarks/nested/run.ps1 -Iterations 7 -Warmups 2
```
