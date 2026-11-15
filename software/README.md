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

`hf mf autopwn [-k KEY]` uses the same automatic MIFARE Classic recovery planner
as the app's **Auto** button, with the built-in factory dictionary and optional
key candidate. It shows the selected methods and sector/key progress, then saves
`<UID>.key`, `<UID>.dic` and `<UID>.bin` in the current folder (or the app console's
chosen folder). Cancellation and failures preserve confirmed keys and any blocks
already read; incomplete recovery returns a failure status.

Search settings can be specified for each `autopwn` run:

| Option | Values | Default |
| --- | --- | --- |
| `--method` | `single-key` (one check at a time), `all-sectors` (batches across sectors), `one-sector` (batches for one sector/key type) | `all-sectors` |
| `--start-sector` | Zero-based sector number on the scanned card | `0` |
| `--sector-order` | `sequential` (wraps around the card), `random` (shuffles the remaining sectors) | `sequential` |

The start sector is always first. For example:

```text
hf mf autopwn --method one-sector --start-sector 3 --sector-order random
```

Every batch is limited to 8 key/sector/A-or-B checks over USB/TCP or 32 over BLE.
These options are also available in Tools → Console and in `hf mf autopwn --help`.
