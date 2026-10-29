# Third-party source

Libraries ChameLab builds from source instead of taking a package. `Directory.Build.targets`
here builds them for net10.0 only, optimized also in Debug, and packs nothing.

## Crapto1Sharp

MIFARE Classic Crypto1 / crapto1 port used for key recovery (mfkey32, mfkey64, nested,
darkside). GPL-3.0, see `Crapto1Sharp/LICENSE`.

- Origin: [kgamecarter/Crapto1Sharp](https://github.com/kgamecarter/Crapto1Sharp) at `ee5f3ce`
  (NuGet 2.0.0), which looks unmaintained.
- Changed here: `CandidateValidationPipeline.EnqueueCurrent` no longer throws
  `OperationCanceledException` when a second match stops the workers while the producer waits
  for room in the queue; mfkey32 then returns "not unique" as intended. Covered by
  `ValidationPipeline_ProducerBlockedOnFullQueue_StopsAfterSecondMatch`.
- Kept: the library (`Crapto1Sharp/Crapto1Sharp`), its tests (`Crapto1Sharp/Crapto1Sharp.Test`),
  `LICENSE` and the original `README.md`. Left out: benchmarks and the translated README.

Its tests run with:

```bash
dotnet test software/external/Crapto1Sharp/Crapto1Sharp.Test
```
