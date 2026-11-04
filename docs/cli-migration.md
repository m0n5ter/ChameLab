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

- [ ] Extract UI-independent Core and shared serial transport.
- [ ] Capture the existing command/argument contract.
- [ ] Implement console host and hardware commands.
- [ ] Implement LF, HF, recovery, EMV, DESFire, SEOS and plotting commands.
- [ ] Replace generators, client build and native/Python tooling.
- [ ] Validate command contracts, protocol bytes, recovery and desktop builds.
- [ ] Remove superseded client sources and update documentation.

Physical hardware verification is reported separately from automated tests.
