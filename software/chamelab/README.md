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
| `ChameLab.Android` | `net10.0-android` | Phone/tablet. BLE (Nordic UART) + TCP transports |
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
- **Dashboard** — model, firmware, git, chip id, BLE address, battery gauge, and a
  Reader/Emulator mode toggle.
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
