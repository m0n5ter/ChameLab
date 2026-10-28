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

The wire format mirrors the reference Python client (`software/script`): a
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
- **Settings** — LED animation, sleep timeout, button functions; apply / save /
  reset.

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

## Hardnested (desktop)

Hardnested recovery is available for hardened-PRNG MIFARE Classic cards when at
least one key is known. Scan the card, find a seed key with the dictionary or
another recovery method, then choose **Hardnested**. ChameLab collects encrypted
nonces with the device, calls the original C solver, authenticates the recovered
key on the same card, and reads the remaining blocks. Keep the card on the reader.

The solver is built as a native library (`.dll`, `.so`, or `.dylib`) and loaded by
`chamelab-hardnested-worker`, a separate process. The worker reports progress over
stdout. Cancel terminates the worker and removes its temporary capture; during
device acquisition, cancellation waits for the current batch response (up to
30 seconds), since the firmware has no command-abort operation. Native crashes
and memory exhaustion do not terminate the GUI. Only one solver runs at a time.

Android, iOS and Browser show a resource-requirements warning instead of enabling
Hardnested. Other recovery methods remain available.

Desktop builds require CMake 3.24+, Git, and a C compiler (MSVC on Windows, GCC or
Clang on Linux, Xcode command-line tools on macOS). The first build fetches the
same XZ/liblzma version used by the CLI, v5.8.1, and links it statically. The
original algorithm's memory requirements remain: the per-nonce state bitsets
alone use 1 GiB, with additional tables and search buffers on top.

`dotnet build ChameLab.Desktop -c Release` builds and copies the native component
to the output's `hardnested/` directory. Windows also detects Visual Studio's
bundled CMake automatically. To use another CMake installation, pass
`-p:CMakeExecutable="PATH/TO/cmake"`. IDE design-time builds skip native compilation.

For cross-platform publishing, build the native component for the target first:

```bash
cmake -S ../src/HardnestedRecovery/native -B native-build -DCMAKE_BUILD_TYPE=Release
cmake --build native-build --config Release --target chamelab-hardnested-worker --parallel
cmake --install native-build --config Release --prefix "$PWD/native-hardnested" --component Hardnested
dotnet publish ChameLab.Desktop -c Release -r YOUR_RID \
  -p:BuildHardnested=false -p:HardnestedNativeDirectory="$PWD/native-hardnested"
```

The CMake build must use the target's architecture/toolchain. Release CI builds
Windows x64, Linux x64, macOS arm64 and macOS x64 components separately and keeps
the library and worker beside the app, including single-file publishes.

To test the native solver without RFID hardware:

```bash
cmake --build native-build --config Release --target chamelab-hardnested-fixture --parallel
ctest --test-dir native-build -C Release --output-on-failure -R '^(missing_input|generate_capture|recover_capture)$'
```

The fixture generator uses the original C Crypto1 implementation to create a
deterministic capture; the test requires recovery of the known 48-bit key.

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
