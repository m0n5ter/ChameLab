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
- **Tools** — Sniff HF (`HF14A_SNIFF`): the Chameleon answers a reader as the card in
  its active slot; both sides are listed and labelled, and MIFARE Classic keys are
  recovered from the reader's authentications (mfkey32 / mfkey64). Proxmark3
  `.trace` load / save. Sniff LF (`LF_SNIFF`): 32 ms of the 125 kHz antenna signal as a
  zoomable waveform, level summary, modulation and clock guess and a Manchester
  decode. Raw `.bin` load / save, so saved captures open without a device.
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

## Hardnested

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

Android and iOS call the native C library through P/Invoke on a background thread.
These embedded builds use a single solver thread, so errors and cooperative
cancellation can unwind safely to the API boundary. All solver allocations and
open capture streams are released on success, failure or cancellation. Progress
is passed back to the UI. Desktop keeps its parallel worker implementation.

The browser runs the same embedded core as a separate WebAssembly module in a
Web Worker. Each attempt gets a fresh worker; Stop terminates it immediately and
releases its memory. Calculations stay on the device. SharedArrayBuffer and
cross-origin isolation headers are not required. Device access still depends on
the browser supporting Web Serial or Web Bluetooth, including on mobile browsers.

Embedded builds cap solver allocations at 1792 MiB. The core's nonce bitsets alone
use 1 GiB, and the app, browser and operating system need additional memory.
Low-memory devices may fail recovery; the budget cannot prevent the operating
system from killing an app under overall memory pressure. Single-thread recovery
can be slower than the desktop worker. Acquisition cancellation still waits for
the device's current batch response on all platforms.

All builds need Python 3, CMake 3.24+, Git and the platform C toolchain. Mobile and
web app builds invoke `../src/HardnestedRecovery/native/build-embedded.py`
automatically. It uses the Android NDK (r28+, `ANDROID_NDK_HOME` or the SDK's
`ndk/` directory), Xcode on macOS for iOS, and an activated standalone Emscripten
SDK 3.1.74+ for the web. The custom Emscripten bundled with .NET is used for the
app runtime, but cannot build this standalone solver's setjmp/longjmp code.
Android supports `android-arm64` and `android-x64`, including 16 KiB page sizes.
iOS builds support `ios-arm64`, `iossimulator-arm64` and `iossimulator-x64`.

```bash
# Android: install the NDK once, then build the app and bundled libraries.
sdkmanager "ndk;28.2.13676358"
dotnet build ChameLab.Android -c Release

# iOS: build on macOS; configure your signing identity for installation on a phone.
dotnet build ChameLab.iOS -c Release -r ios-arm64

# Web: the solver module is copied to wwwroot/hardnested/ during publish.
# Install/activate emsdk 3.1.74 and source emsdk_env.sh (emsdk_env.bat on Windows).
dotnet publish ChameLab.Browser -c Release
```

To reuse prebuilt components, pass `-p:BuildHardnested=false` and
`-p:HardnestedNativeDirectory=/absolute/path/to/output`. That directory contains
ABI subdirectories with `.so` files on Android, the solver and liblzma `.a`
archives on iOS, or `hardnested.js` and `hardnested.wasm` for the browser. The build
fails if the required files are absent. CI verifies known-key WebAssembly
recovery and builds Android plus unsigned iOS device/simulator apps; iOS device
installation still requires signing.

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
