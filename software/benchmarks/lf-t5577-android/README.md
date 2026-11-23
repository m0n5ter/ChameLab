# LF T5577 write/read test on Android BLE

This separate diagnostic APK runs the existing `ChameLab.Cli.Commands` through
the application's actual `Mobile/BleTransport.cs`. ADB installs/starts the APK
and collects files; every Chameleon command travels over Nordic UART BLE.
The Chameleon can be powered from a wall adapter. Keep the T5577 stationary
on its LF antenna throughout the run.

**This test repeatedly overwrites the T5577.** The schedule must contain test
identities, and the tag must be writable with password `20206666`. No permanent
lock bits or emulator slots are written. The runner restores and verifies the
original Pyramid frame `0001014A3E0101010101016BE38CD64A` in `finally` while
the BLE connection remains usable, then restores the previous device mode.
The restoration outcome is recorded; a disconnected device cannot be restored.

Build with a schedule JSON (an array of `{ "phase": "smoke", "case": ... }`;
case fields are `name`, `family`, `tag`, `uid`, `write`, `scan`, and optional
`rate`/`scan_data`). `write: null` invokes the existing Pyramid writer.
IDTECK always uses native PSK1 command 3022. The runner never changes IDTECK
to another modulation for verification.

```powershell
dotnet build software/benchmarks/lf-t5577-android -c Debug -p:LfT5577Schedule=C:\path\schedule.json
adb install --no-incremental -r software/benchmarks/lf-t5577-android/bin/Debug/net10.0-android/android-arm64/io.github.m0n5ter.chamelab.lft5577bench-Signed.apk
adb shell pm grant io.github.m0n5ter.chamelab.lft5577bench android.permission.BLUETOOTH_SCAN
adb shell pm grant io.github.m0n5ter.chamelab.lft5577bench android.permission.BLUETOOTH_CONNECT
adb shell am start -n io.github.m0n5ter.chamelab.lft5577bench/.MainActivity --es address CE:BA:62:C5:67:98
adb logcat -s LfT5577Bench:I '*:S'
adb exec-out run-as io.github.m0n5ter.chamelab.lft5577bench cat files/results.json
```

Use `--es phase smoke --ei limit 5` for a short initial probe.
`--es family PAC --ei repeat 5` tests just PAC with repeated schedule entries;
`--es family IDTECK` uses the native PSK1 reader. An optional `--ei limit N`
limits the expanded schedule. The default still runs the complete schedule once.
After a completed run, collect files before starting again: a new run replaces its JSON logs.
Stop the APK with `am force-stop` only after `LF_BLE_COMPLETE` to allow cleanup.

Each cycle writes a different scheduled identity and performs two independent
native reads. It compares every ID byte, including EM410x's reported type,
Electra's full 13 bytes and IDTECK's full eight bytes. HID compares all 13 ID
bytes while ignoring the appended decoded bit length. RF misses, wrong IDs,
command errors and timeouts remain visible; the runner does not retry failed
reads. Pyramid's normal writer also performs its own built-in verification.

`files/cycles.jsonl` and `files/commands.jsonl` are appended after each cycle
and command. `files/results.json` contains progress, totals, per-format results,
slot snapshots and restoration status. Up to ten failed cycles also preserve
ADC captures (`failed-N.bin`). A lost BLE link stops the run immediately.
