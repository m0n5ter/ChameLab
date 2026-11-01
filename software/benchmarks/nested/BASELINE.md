# Nested benchmark — 2026-11-01

Measured on Intel Core i9-13900H (14 cores / 20 logical CPUs), Windows x64
10.0.26100. C: MSVC toolset 14.51.36231, C11 `/O2`.
C#: .NET SDK 10.0.402, runtime 10.0.14, Release, optimizing JIT with tiering
and ReadyToRun disabled. Sources at commit
`f7a6cd533d3ef61fe2828822397fb5a9b784788b`.

Median elapsed milliseconds per full recovery; two warmups and seven measured
calls per cell:

| Known key | C, 1 thread | C, up to 4 threads | C# |
| --- | ---: | ---: | ---: |
| `000000000000` | 2409.82 | 1114.51 | 4127.62 |
| `FFFFFFFFFFFF` | 2127.64 | 820.27 | 3219.64 |
| `A0A1A2A3A4A5` | 1598.41 | 973.72 | 2464.35 |
| `112233445566` | 2137.71 | 735.50 | 3106.88 |
| Sum of per-case medians | 8273.58 | 3643.99 | 12918.48 |

Using the ratio of the sums of the per-case medians, C is **1.56× faster**
than C# with sequential workers, and **3.55× faster** with the original
parallel workers. These ratios describe this four-fixture workload, not a
general property of the languages.

All 108 recoveries returned the expected key and stable candidate sets.
All twelve fixture/mode candidate sets exactly matched the independent C
reference (21, 3, 1 and 2 keys respectively; order ignored).

There was substantial timing variation: for example, the first C# fixture
ranged from 3322.53 to 5833.07 ms. This run did not isolate the machine or pin
cores, so small differences should not be interpreted as precise effects.
Raw samples and candidate lists are in [baseline.json](baseline.json).
Methodology, compiler differences and reference-C limitations are described
in [README.md](README.md).

The command now runs the optimized managed sources. Reproducing this old
baseline requires the original managed sources from the commit above and
the original sequential managed benchmark mode.

Current runner command from the repository root:

```powershell
./software/benchmarks/nested/run.ps1 -Iterations 7 -Warmups 2
```
