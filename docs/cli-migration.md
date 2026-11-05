# C# CLI migration

Scope: share the .NET protocol, models and recovery engines between the app and
CLI, replace the Python CLI and native client solvers, and migrate client tools.
Firmware and its embedded toolchain remain separate; browser interop remains web
code. JSON, XML/XAML, YAML, binary resources and license notices are retained.

Completion requires all existing command families, flags and file formats,
interactive history/completion, repeated `--command`, useful exit codes,
cancellation, desktop publishing and independent recovery fixtures. Old client
sources must remain until replacement behavior has been verified.

## Progress

- [x] Extract UI-independent Core and shared serial transport.
- [x] Capture the existing command/argument contract (113 original command paths; 115 after merging EM4305 additions).
- [x] Implement console host and hardware commands.
- [x] Implement LF, HF, recovery, EMV, DESFire, SEOS and plotting commands.
- [x] Replace generators, client build and native/Python tooling.
- [x] Validate command contracts, protocol bytes, recovery and desktop builds.
- [x] Remove superseded client sources and update documentation.

Physical hardware verification is reported separately from automated tests.

## Verified on 2026-11-05

- CLI and desktop Release builds: no warnings/errors. Standalone Windows CLI
  publishes and runs `dump_help` and repeated `--command`.
- After merging current main: 485 application tests and 54 CLI/tool tests pass.
- All 115 catalog commands have registered handlers and offline help.
- Protocol tests cover raw frame flags/bits, reader/slot restoration, cancellation,
  Ultralight ACK, Electra writes, and partial auth traces with known-key decryption.
- Independent OpenSSL-generated vectors cover both ULC nonce generators and all
  four key segments. Corrected USCUID nonce byte order.
- C# table importer reproduced the exact 9,966,996-byte resource from all 351
  upstream files, checking compressed, decoded and packed SHA-256 hashes.
- C# circuit generator reproduces scalar/SIMD sources; CI can check for drift.
- Release workflow now publishes self-contained .NET CLI for Windows, Linux and macOS.

## Additional verification and replacements

- All 20 original native reference vectors were regenerated with GCC-built C
  solvers through the C# orchestrator; values and full candidate multisets match.
- Standalone Linux CLI publishes and runs under WSL. Windows terminal smoke test
  covers Tab completion, command execution, Ctrl+C and exit. The waveform child
  process starts without an immediate failure; visual interaction is not certified.
- Desktop and Browser Release builds pass with zero warnings/errors (browser
  build used RunAOTCompilation=false). macOS execution awaits its CI runner.
- C# tools replace reference orchestration, benchmarks, MFC4K reading/reporting,
  release naming and PR lifecycle logic. PR lifecycle tests use an in-memory HTTP
  handler; no live PR/comment actions were executed during migration.
- Python/native client sources, native fixture sources and Python packaging files
  have been removed. Historical reproduction uses the pinned checkout documented
  in ChameLab.Tests/Reference/README.md; independent fixture data remains.
- Firmware maintenance/DFU scripts now reside under firmware/tools. Browser worker
  tests reside under ChameLab.Browser/tests. check-languages audits these explicit
  source exceptions in CI. YAML, XAML/XML and other declarative configuration remain.

## Validation limits

Physical-card/reader parity is not certified by these automated checks. In
particular, DESFire, SEOS, EMV readers and destructive ULCG key-page recovery must
be exercised on appropriate test hardware before a stable release. macOS runtime,
full browser AOT publishing and visual waveform interactions await platform testing.
Release workflows have been updated but have not been executed on GitHub here.

Implemented on `VK_Csharp_CLI` and merged into `main`. The merge preserves newer
translations, GUI library import, web release packaging and EM4305 commands.
Release announcements are now C#; browser-only font subsetting lives under
ChameLab.Browser/tools and uses the relocated Core translations.
