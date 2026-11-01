# Nested: C vs C#

Run on Windows x64 with .NET 10 SDK and Visual Studio C++ build tools:

```powershell
./software/benchmarks/nested/run.ps1 -Iterations 7 -Warmups 2
```

The runner builds MSVC C11 `/O2` and .NET Release. The managed project links
the actual application sources, without Avalonia or third-party packages.
It compiles the original `nested_util.c`, `crapto1.c`, `crypto1.c`,
`bucketsort.c` and `parity.c` unchanged. `native.c` reproduces the nonce
sweep from `nested.c` and exports the candidate list through P/Invoke.

Modes:

- **C/1**: the original four worker partitions executed synchronously.
- **C/4**: the original workers run on up to four Windows threads.
- **C#/1**: the optimized managed recovery with one worker.
- **C#/4**: the optimized managed recovery capped at four workers.
- **C#/all**: the application's `NestedRecovery.Recover`, using all available
  logical processors, up to the number of independent nonce candidates.

The managed recovery streams states directly into key rollback, reuses each
worker's recovery workspace, groups states by contribution byte in linear
time, and counts repeated keys after a six-pass byte-radix sort of a flat array.
A 20 KiB table for the linear plaintext rollback/key permutation replaces
bitwise rollback per state. Its four input-byte contributions are calculated
once per nonce candidate; each state needs six table lookups and XORs.
Workers acquire the next nonce dynamically. Each worker owns
its buffers. Browser builds use one worker. The public Crypto1 list/visitor
producers retain their previous state ordering.

Use `-Modes 'C#/1,C#/4,C#/all'` for a managed-only exploratory run. The full default
run is the basis of the comparison in `RESULTS.md`.

Validation:

```powershell
dotnet test software/chamelab/ChameLab.Tests/ChameLab.Tests.csproj -c Release
```

The suite checks the unchanged public recovery characterization, complete
state multisets (including duplicate and zero states) for the grouped path,
and deterministic sequential/parallel Nested candidates.
It also checks all 2,560 rollback byte contributions, 4,096 mixed states,
and radix sorting against comparison sorting at the fallback threshold and
on large arrays with repeated, extreme, sorted and reversed keys.

The local `pthread.h` implements only the create/join operations needed by
Nested, using `_beginthreadex`. C/1 keeps the original worker partitions,
including their candidate accumulation behavior; it changes scheduling only.
Calls to the native wrapper must not overlap.

All four independent synthetic Nested fixtures from
`ChameLab.Tests/Reference/crypto_vectors.json` are used, without hardware.
Every invocation must recover the known key. All candidate sets must also
match the saved C reference, and each mode must return stable candidate sets.
The JSON reports whether managed candidate sets match C too.

Each fixture/mode gets two untimed warmups and seven measured calls by default.
Execution order rotates between rounds. Timings cover the full nonce sweep,
state recovery, rollback and candidate aggregation, including allocations,
native worker creation/join, and the small P/Invoke boundary. Input loading,
validation, printing, build and process startup are excluded. No forced GC is
used; GC pauses during recovery are included. Tiered compilation and
ReadyToRun are disabled so the managed measurements use optimizing JIT code
after warmup. This measures warm recovery, not first-use latency.

`results.json` contains all measured samples and candidate sets. Interpret
results as a comparison of these implementations on this machine, not as a
general language benchmark. In particular, C and C# use different candidate
aggregation strategies, both /4 modes are parallel, and MSVC does not enable the
GCC-only filter lookup table in the original `crapto1.c`.

The original C has a candidate-count allocation that is not freed and a
one-past-end read in `uniqsort`. These are retained in the unchanged reference
sources. The harness is intended for bounded runs, not memory comparisons or
long-running stress tests.

`baseline.json` and `BASELINE.md` preserve the measurements made before the
managed Nested optimization. The original report's raw-data link refers to
`baseline.json` now.

`four-worker.json` and `FOUR_WORKER.md` preserve the previous managed version
with the four-worker cap, bitwise rollback and comparison key sort.
`results.json` and `RESULTS.md` describe the current version.
