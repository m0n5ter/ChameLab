# Crypto1 / crapto1

MIFARE Classic Crypto1 and the crapto1 key recovery (mfkey32, mfkey64, nested, darkside) used by
the key recovery services. Namespace `ChameLab.Crypto`.

Taken from [Crapto1Sharp](https://github.com/kgamecarter/Crapto1Sharp) by kgame, commit
`ee5f3ce` (NuGet 2.0.0), GPL-3.0 like ChameLab. Upstream looks unmaintained, so the code lives here.

Changes since:

- Namespace `Crapto1Sharp` → `ChameLab.Crypto`, .NET 10 only (the netstandard / net462 branches
  are gone).
- `CandidateValidationPipeline.EnqueueCurrent` no longer throws `OperationCanceledException` when
  a second match stops the workers while the producer waits for room in the queue: mfkey32 then
  reports "not unique" as intended.

Its tests are in `ChameLab.Tests/Crypto`, converted from MSTest to xUnit.

In Debug builds this code is not optimized, so key recovery and its tests run about twice as slow
as in Release.
