# Nested: automatic CPU parallelism and further optimization — 2026-11-01

Intel Core i9-13900H (14 cores / 20 available logical processors), Windows
x64 10.0.26100. C: MSVC toolset 14.51.36231, C11 `/O2`. C#: .NET SDK
10.0.402, runtime 10.0.14, Release; tiering and ReadyToRun disabled.
The managed working tree is based on commit
`f7a6cd533d3ef61fe2828822397fb5a9b784788b`; native algorithm sources are unchanged.

Two warmups and seven measured recoveries per cell, with rotating execution
order and no concurrent test run. Values are median elapsed milliseconds.

| Known key | Nonce tasks | C/1 | C#/1 | C/4 | C#/4 | C#/all |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `000000000000` | 9 | 2041.58 | 1817.18 | 828.36 | 638.83 | 326.22 |
| `FFFFFFFFFFFF` | 9 | 1995.93 | 1747.54 | 777.93 | 663.24 | 319.23 |
| `A0A1A2A3A4A5` | 7 | 1574.80 | 1417.15 | 1008.60 | 532.90 | 336.39 |
| `112233445566` | 8 | 1774.09 | 1595.17 | 617.05 | 517.49 | 331.69 |
| Sum of case medians | | 7386.42 | 6577.03 | 3231.94 | 2352.45 | 1313.53 |

Ratios of the sums of per-case medians:

- The new default, **C#/all**, is **2.46× faster than C/4**.
- In this run C#/all is **1.79× faster than the new C#/4**.
- With one worker, C# is **1.12× faster than C**.
- Compared with the previous managed version capped at four workers, the new
  default is **2.09× faster**. The new four-worker mode alone is **1.17× faster**
  than that previous version, showing improvement beyond adding workers.
- Against the original sequential managed baseline, the new default is
  approximately **9.83× faster**. Before/after ratios use separate runs and
  include algorithm changes and increased parallelism.

## CPU use

The public `NestedRecovery.Recover` now uses `Environment.ProcessorCount`
instead of a four-worker limit. Work is dynamically acquired from the nonce
queue. Each active worker owns its recovery buffers and key list.

On this machine the limit is 20 logical processors. These captures expose
only 7–9 independent nonce candidates, so actual recovery parallelism is
7–9 workers. Full recovery parallelism is bounded by available processors
and independent nonce tasks. Browser runtimes keep their sequential path.
Production remains entirely managed; the C DLL is used only by the benchmark.

## Further changes

- Plaintext rollback followed by the key-bit permutation is linear over GF(2).
  A 20 KiB set of byte tables, generated from the existing reference primitive,
  replaces the bitwise rollback/key extraction for every state. Input-byte
  contributions are computed once per nonce; each state requires six table
  lookups and XORs.
- Candidate keys use six byte-radix passes over their 48 bits, with a pooled
  scratch array and comparison sorting for small inputs.
- Dynamic nonce assignment balances variable recovery costs across workers.
- The previous linear state grouping, reusable workspaces, deterministic
  candidate ranking and cancellation handling are retained.

## Validation and interpretation

The complete Release suite passed: **374 tests**. New tests cover all 2,560
byte contributions of the rollback map, 4,096 mixed full-width states/inputs,
sorting at the fallback boundary, duplicates, extremes, sorted/reversed
arrays, cancellation, and equality of sequential/default-parallel candidates.
The earlier grouped-state multiset and public characterization tests also pass.

All **180 benchmark recoveries** returned the known key and exactly the
saved independent C candidate set. Candidate counts are 21, 3, 1 and 2,
respectively. Candidate sets remained stable across every mode and repetition.
Benchmark Release compilation completed with zero warnings/errors.

These figures describe four synthetic fixtures and these implementations on
this machine. C#/all has more workers than C/4. No core pinning or machine
isolation was used. MSVC leaves the GCC-only C filter lookup disabled. Timings
exclude process startup and validation; methodology and native-reference
limitations are in [README.md](README.md).

[Raw samples](results.json) · [Previous four-worker report](FOUR_WORKER.md) ·
[Previous four-worker samples](four-worker.json) · [Original baseline](BASELINE.md)

From the repository root:

```powershell
./software/benchmarks/nested/run.ps1 -Iterations 7 -Warmups 2
```
