# ChameLab — Avalonia GUI for Chameleon Ultra

A cross-platform desktop + mobile client for the Chameleon Ultra, built with the
latest **Avalonia 12.1**, **ReactiveUI** (ReactiveUI.Avalonia 12.1.6) and the
**Actipro Modern Theme** (26.2). It replaces the older GUI with a single
responsive UI that adapts from a docked side-navigation on desktop to an overlay
drawer on phones.

## Projects

| Project | Target | Notes |
| --- | --- | --- |
| `ChameLab` | `net10.0` | Shared core: protocol, transports, view models, views, theme |
| `ChameLab.Desktop` | `net10.0` | Windows/macOS/Linux. Serial (USB-CDC) + TCP transports |
| `ChameLab.Android` | `net10.0-android` | Phone/tablet. USB CDC (OTG) + BLE (Nordic UART) + TCP transports |
| `ChameLab.iOS` | `net10.0-ios` | Phone/tablet (build on macOS). BLE + TCP |
| `ChameLab.Browser` | `net10.0-browser` | WebAssembly build. Web Serial + Web Bluetooth + TCP |

Package versions are centralized in [`Directory.Packages.props`](Directory.Packages.props).

## Architecture

```
Transport  (ITransport / ITransportScanner)
  ├─ SerialTransport    (Desktop, System.IO.Ports, auto-detects VID 0x6868)
  ├─ BleTransport       (Android/iOS, Plugin.BLE over Nordic UART Service)
  ├─ WebSerialTransport (Browser, navigator.serial via JS interop)
  ├─ WebBluetoothTransport (Browser, navigator.bluetooth over Nordic UART Service)
  └─ TcpTransport       (Core, host:port — bridge / simulator)
        │
ChameleonConnection    frame codec (SOF/LRC) + async request/response loop
        │
ChameleonClient        typed command API (device info, slots, scan, MFC, settings)
        │
DeviceService          connection state + scanner registry (AppServices composition root)
        │
ViewModels (ReactiveUI) → Views (Actipro ModernTheme, adaptive SplitView shell)
```

The app and C# CLI share the protocol implementation in `ChameLab.Core`: a
big-endian frame `SOF | LRC1 | CMD | STATUS | LEN | LRC2 | DATA | LRC3`, with
responses matched to requests by command id.

## Features

- **Connect** — auto-scan serial ports (Chameleon VID flagged first) or a manual
  `host:port` network bridge.
- **Chameleon** — takes the place of Connect once a device is attached: model,
  firmware, git, chip id, BLE address, battery gauge, a Reader/Emulator mode
  toggle, firmware updates and the device settings (LEDs, sleep, buttons, BLE
  pairing, resets).
- **Slots** — all eight emulation slots as cards (HF/LF type + nickname), tap to
  activate, save to flash.
- **Read** — HF (ISO14443-A) and LF (EM410x) scans with decoded UID/ATQA/SAK/ATS.
  MIFARE Classic cards (Mini / 1K / 4K) can then be dumped with a key dictionary,
  edited as hex and loaded into the active slot to emulate them.
- **Saved cards** — local card library (create / edit / duplicate / delete,
  search, color tags). Save straight from **Read**, import Flutter
  ChameleonUltraGUI JSON, Proxmark3 JSON, Flipper `.nfc` / `.rfid`, MCT, `.bin` /
  `.eml`, export to JSON (same format as the Flutter app) and MIFARE Classic
  memory to `.bin` / `.eml`. Load any card into one
  of the eight slots — MIFARE Classic (blocks), Ultralight / NTAG (pages, version,
  signature, counters), ISO14443-4 and LF (EM410x, HID Prox, ioProx, Viking,
  PAC, Jablotron, IDTECK). Stored under `LocalApplicationData/ChameLab`
  (`localStorage` in the browser build).
- **mfkey32** — available in the card editor's attack toolbar after loading a
  MIFARE Classic card into a slot. The progress panel guides capture from a reader,
  saves recovered keys under the card's full UID, and waits for the original
  card on Chameleon before automatically reading it. Stop keeps recovered keys and any
  blocks already read; completion shows a green Done button. The previous device
  mode, active slot and emulator settings are restored when the operation ends.
- **Tools** — Sniff HF (`HF14A_SNIFF`): the Chameleon answers a reader as the card in
  its active slot; both sides are listed and labelled, and MIFARE Classic keys are
  recovered from the reader's authentications (mfkey32 / mfkey64). Proxmark3
  `.trace` load / save. Sniff LF (`LF_SNIFF`): 32 ms of the 125 kHz antenna signal as a
  zoomable waveform, level summary, modulation and clock guess and a Manchester
  decode. Raw `.bin` load / save, so saved captures open without a device.
- **Settings** — LED animation, sleep timeout, button functions; apply / save /
  reset.

**T55XX password recovery** is available in Tools and when writing an LF card.
Choose 4-byte T55XX dictionaries using the key selector; MIFARE Classic dictionaries
remain separate. Each candidate is checked by writing a different test ID and reading
it back. Tools then restores and verifies the original ID; the card writer writes and
verifies the requested ID. Both show the matching key, progress and Cancel. An empty
new-key field uses `20206666`. Cancellation waits for an active probe to be verified
and the original ID restored before returning. Keep the tag on the reader until this
finishes. Recovery needs firmware with raw T55XX block writes and a readable LF format;
normal writing also supports blank, unprotected tags.

## Build & run

```bash
# Desktop (primary target)
dotnet run --project ChameLab.Desktop -c Debug
```

```bash
# WebAssembly (needs the wasm-tools workload for native Skia linking)
dotnet workload install wasm-tools
dotnet run --project ChameLab.Browser -c Debug
```

For browser recovery performance, use a published Release build:

```bash
dotnet publish ChameLab.Browser -c Release
```

Release publishing enables managed WebAssembly AOT, including the recovery code
executed in Web Workers. This increases publish time and download size but avoids
interpreting the CPU-heavy Crypto1 loops. Debug builds keep the interpreter for
fast iteration. Pass `-p:RunAOTCompilation=false` to publish without AOT when
comparing performance or investigating build issues.

```bash
# Android (needs the matching Android SDK platform installed once)
dotnet build -t:InstallAndroidDependencies -f net10.0-android ChameLab.Android
dotnet build ChameLab.Android -c Debug
```

iOS builds on macOS with the `ios` workload.

### Android USB connection

On a phone or tablet with USB Host / OTG support, connect the Chameleon using a
USB data cable (and an OTG adapter if needed). Open Connect, select the
`Chameleon (USB …)` entry and allow Android's USB access request. Devices appear
automatically while the Connect page is open; Rescan also refreshes the list.
Charge-only cables cannot carry the connection. Phones without USB Host support
can still connect over BLE.

The Android USB transport supports normal device commands, scans and card dumps.
Firmware updates on Android currently use BLE; USB bootloader flashing is not
implemented in the Android build. iOS continues to use BLE and TCP.

## Reusing recovered keys

Nested, static nested, and hardnested first try the current card's known unique
keys for each missing sector key, checking both A and B independently. Each
successful authentication raises that key's priority for subsequent checks in
the recovery run; keys with more successful uses are tried first. An attack
runs only if those keys do not authenticate. Each confirmed key is immediately
saved to **Known card keys** under the card's full UID and becomes available for
subsequent sectors. A final pass tries newly found keys on earlier unresolved
sectors without repeating combinations already rejected by the card. RF errors
are retried and are not remembered as wrong keys.

## Static encrypted nested

Choose **Static encrypted nested** in the card or target-card recovery panel
for FM11RF08S and compatible backdoored MIFARE Classic cards. A known sector
key is not required. ChameLab tries backdoor keys `A396EFA4E24F`, `A31667A8CEC1`
and `518B3354E760`, collects the static encrypted nonces, calculates candidates
locally, and checks them on the card in batches. RF08S seed filtering prioritizes
candidate pairs; remaining candidates are tried if that filter does not find a
key. This managed solver works on every platform and requires firmware commands
2014 and 2015. Keep the card on the reader throughout recovery. Stop preserves
recovered keys; an in-flight collection or key batch finishes before stopping.

The solver streams recovered states, uses table-based key rollback and seed
filtering, and checks the last parity bit directly. Reproducible CPU benchmarks
and validation are in [benchmarks/static-encrypted](../benchmarks/static-encrypted/README.md).

## Hardnested

Hardnested recovery is available for hardened-PRNG MIFARE Classic cards when at
least one key is known. Load known keys or recover one, then choose **Hardnested**.
The app collects encrypted nonces and validates a recovered key against the card.

All platforms use `ManagedHardnestedEngine`, a fully managed C# port of Proxmark3
commit `1b71dfbbd4c156c34c02cfdd9140470d97d1336c`. Portable SIMD has a scalar
fallback. The same sources run on Windows, Linux, macOS, Android, iOS and .NET
WebAssembly; hardnested adds no native library, C compiler, NDK, standalone
Emscripten SDK or compression library requirement to the app build.

Desktop and mobile calculations use a background task with cooperative
cancellation and progress. Browser calculations run in a separate .NET Web Worker
without starting Avalonia; Stop terminates that worker immediately. No server,
SharedArrayBuffer or cross-origin isolation headers are needed. Acquisition
cancellation still waits for the device's current response.

State tables and intermediate pages are compact and shared. Memory and execution
time depend on the capture; more distinct nonce prefixes usually reduce the
remaining search. Devices still need sufficient free memory for recovery.

Build normally with the .NET SDK and the app platform's workload:

```bash
dotnet build ChameLab.Desktop -c Release
dotnet build ChameLab.Android -c Release
dotnet build ChameLab.iOS -c Release -r ios-arm64  # on macOS
dotnet publish ChameLab.Browser -c Release
```

The browser publish uses .NET's own AOT toolchain. iOS device installation still
requires Apple signing. Tables are embedded in the shared assembly and cannot be
omitted by a missing platform-specific packaging step.

Source provenance, licensing and table regeneration are documented in
[Crypto/Hardnested/NOTICE.md](ChameLab.Core/Crypto/Hardnested/NOTICE.md). Run the tests
with `dotnet test ChameLab.Tests -c Release`. Reproducible performance and browser
worker checks live in [../benchmarks/hardnested](../benchmarks/hardnested).
The Python CLI retains its existing native solver.

## Web Serial (browser)

The WASM build cannot use `System.IO.Ports` — browsers only reach USB-serial
devices through the Web Serial API. [`wwwroot/serial.js`](ChameLab.Browser/wwwroot/serial.js)
bridges `navigator.serial`, and [`WebSerial.cs`](ChameLab.Browser/WebSerial.cs)
wraps it as an `ITransport`. Clicking **Rescan** lists already-granted ports and,
if none, opens the browser's port chooser (ports granted this way persist for the
origin). Requires a Chromium browser (Chrome/Edge) over `https://` or
`http://localhost`; Firefox/Safari do not implement Web Serial.

## Web Bluetooth (browser)

Where the browser has `navigator.bluetooth` (Chrome/Edge on Windows, macOS,
ChromeOS and Android), the connect page shows a **Pair over Bluetooth** button that opens
the browser's device chooser, filtered to Chameleons. [`wwwroot/ble.js`](ChameLab.Browser/wwwroot/ble.js)
talks to the Nordic UART Service and [`WebBluetooth.cs`](ChameLab.Browser/WebBluetooth.cs)
wraps it as an `ITransport`. A page cannot scan in the background, so picked
devices are listed whether or not they are awake; connecting to a sleeping one
times out with a hint to wake it. Devices picked earlier are listed again after
a reload only where the browser implements `getDevices()`. Writes go out in
20-byte chunks because the page cannot see the negotiated MTU. Firmware updates
are not available over Web Bluetooth.

## Status

- Desktop builds and runs.
- Browser builds and runs; the `wasm-tools` workload is required so SkiaSharp's
  native assets are linked, and DevTools is omitted (not WASM-safe).
- Android/iOS build once their platform SDKs are present (API level / macOS).

## Bluetooth LE (mobile)

Android and iOS connect wirelessly over the firmware's Nordic UART Service
(`6E400001-…`). The shared implementation lives in [`Mobile/BleTransport.cs`](Mobile/BleTransport.cs)
and is compiled into both heads. The scanner matches devices whose name starts
with "Chameleon" or that advertise the Nordic UART service.

- Android: permissions are declared in the manifest and requested at runtime
  (`BLUETOOTH_SCAN`/`BLUETOOTH_CONNECT` on API 31+, location on ≤30).
- iOS: `NSBluetoothAlwaysUsageDescription` is set in `Info.plist`.

## Follow-ups

- Light-theme polish and localization.
