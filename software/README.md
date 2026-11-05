# Chameleon Ultra CLI

Requires the .NET 10 SDK for development. Published CLI packages include the runtime.

```text
dotnet run --project software/chamelab/ChameLab.Cli -c Release
dotnet run --project software/chamelab/ChameLab.Tools -c Release -- --help
```

Run commands from the repository root. The former MFC4K PowerShell/Python helper
is now available as C#:

```text
dotnet run --project software/chamelab/ChameLab.Tools -c Release -- read-mfc4k --port COM8 --output mfc4k-results
```

It preserves partial dumps and a JSON report, supports dictionary keys and
managed nested recovery, and restores the original device mode on exit.
