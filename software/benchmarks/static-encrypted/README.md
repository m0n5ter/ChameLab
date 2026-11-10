# Static encrypted nested performance

The solver was optimized again on 2026-11-10. See the current
[three-attack GUI comparison](../recovery/README.md). The recorded table below
describes the previous version. Current recovery uses up to eight workers;
the runner now counts allocations across all threads.

Run with the .NET 10 SDK (no card or native compiler required):

```powershell
dotnet run --project software/chamelab/ChameLab.Tools -c Release -- benchmark static-encrypted 7 2
```

The benchmark calls the actual application assembly. `BaselineRecovery.cs`
preserves the previous managed solver from commit `b581a209`, with only its
namespace and class name changed. This compares two managed implementations;
it does not compare C with C#.

Each case receives two warmups and seven measured calls in Release. Execution
order alternates between baseline and optimized calls. Tiered compilation and
ReadyToRun are disabled. Timings include recovery/filtering, allocations and
buffer cleanup; startup, build, validation and printing are excluded. No forced
GC is used. Allocation counts cover all managed threads.

Every invocation must match the baseline's complete candidate set. Recovery
validation sorts candidates outside the measured region; filtering must preserve
the exact input order and duplicates. The original C example additionally checks
its saved count and fingerprint, and synthetic captures must recover their known
keys. The C example happens to match the all-ones-key capture, so those rows are
not independent fixtures.

## Recorded results

Windows x64, .NET 10.0.14, 20 logical CPUs; both solvers use one thread.
Medians from [results.json](results.json):

| Operation / case | Before, ms | After, ms | Speedup |
| --- | ---: | ---: | ---: |
| Recovery: C example | 289.34 | 202.06 | 1.43× |
| Recovery: key `000000000000` | 356.28 | 255.35 | 1.40× |
| Recovery: key `FFFFFFFFFFFF` | 286.99 | 210.27 | 1.36× |
| Recovery: key `112233445566` | 515.36 | 364.43 | 1.41× |
| Filter: 65,536 keys on each side | 34.17 | 4.87 | 7.01× |
| Filter: 65,536 candidates, one known other key | 14.89 | 1.97 | 7.55× |

These measure local CPU work, not end-to-end recovery time with a physical card.
Device acquisition, transport and authentication can dominate that time.
Browser and mobile performance have not been measured here.

## Implementation

Recovery streams states through the existing contribution-grouping producer,
checks the next filter bit directly, and uses `NestedKeyTransform` for rollback.
This removes the intermediate state list, full-word parity generation and
bitwise rollback per accepted state. Deduplication and zero-state exclusion are
retained; contribution grouping changes candidate enumeration order.

Seed filtering composes eight inverse PRNG steps into one table lookup and
calculates the nonce's initial 14 steps once per list. Composition preserves the
reference's special behavior for zero, including zero reached after mixing a key
byte. A 65,536-entry boolean table replaces hashed seed membership checks.

Recovery allocates 2 MiB less managed memory per measured call. It also rents an
additional 8 MiB grouping buffer, cleared and returned in `finally`; allocation
reduction is not a claim of lower peak or retained pooled memory. The seed jump
table adds 128 KiB of shared read-only data. A filter call allocates a 64 KiB
membership table, including when the other side has one known key.

## Regression checks

```powershell
dotnet test software/chamelab/ChameLab.Tests/ChameLab.Tests.csproj -c Release
```

The suite checks the full C candidate fingerprint, differential recovery against
the old producer/word-parity/rollback path, zero and extreme states, all 65,536
nonce prefixes with zero and mixed keys, every key-byte contribution, filtering
order/duplicates/empty inputs, cancellation and existing card workflows.
