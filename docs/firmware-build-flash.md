# Building and flashing firmware locally

This is the shortest path to build uncommitted firmware changes and flash a connected ChameleonUltra or ChameleonLite over USB. An application-only DFU update (`*-dfu-app.zip`) preserves slots, cards and settings.

## Prerequisites

- **Docker** (uses `ghcr.io/rfidresearchgroup/chameleonultra-fw-builder:main`, which bundles `arm-none-eabi-gcc` and `nrfutil nrf5sdk-tools`).
- **One of** for flashing over USB:
  - **ChameLab Desktop** (no external flashing tools needed), or
  - **`.NET 10+ SDK`** (uses `ChameLab.Core` + `ChameLab.Serial` directly over USB), or
  - **`nrfutil`** (`nrfutil install device`).

## 1. Build the DFU application package (fast incremental)

`firmware/build.sh` (and `docker compose up build-ultra`) deletes `firmware/objects/` and rebuilds the bootloader, SoftDevice merges and full recovery packages every time. When iterating on application code, compile only `firmware/application` incrementally and pack `*-dfu-app.zip`:

### Windows (PowerShell, from repo root)

```powershell
docker run --rm -v "${PWD}:/workdir" -e CURRENT_DEVICE_TYPE=ultra `
  ghcr.io/rfidresearchgroup/chameleonultra-fw-builder:main bash -ec `
  "rm -f firmware/objects/application/app_cmd.c.o && make -C firmware/application -j && nrfutil nrf5sdk-tools pkg generate --hw-version 0 --key-file resource/dfu_key/chameleon.pem --application firmware/objects/application.hex --application-version 1 --sd-req 0x0100 firmware/objects/ultra-dfu-app.zip"
```

### Linux / macOS (Bash, from repo root)

```bash
docker run --rm -v "$PWD:/workdir" -e CURRENT_DEVICE_TYPE=ultra \
  ghcr.io/rfidresearchgroup/chameleonultra-fw-builder:main bash -ec '
    rm -f firmware/objects/application/app_cmd.c.o
    make -C firmware/application -j
    nrfutil nrf5sdk-tools pkg generate \
      --hw-version 0 --key-file resource/dfu_key/chameleon.pem \
      --application firmware/objects/application.hex --application-version 1 \
      --sd-req 0x0100 firmware/objects/ultra-dfu-app.zip
  '
```

Notes:
- Deleting `app_cmd.c.o` forces only `app_cmd.c` to recompile so the embedded `GIT_VERSION` (including `firmware/VERSION` and `-dirty`) stays current.
- **ChameleonLite**: change `CURRENT_DEVICE_TYPE=lite`, `--hw-version 1`, and `firmware/objects/lite-dfu-app.zip`. When switching between Ultra and Lite, clear cached object files first (`rm -rf firmware/objects/application`).
- **Full clean build** (bootloader changes or release archives): run `cd firmware && docker compose up build-ultra` (or `build-lite`). Add `--pull=always` only when updating the builder image.

## 2. Flash the connected Chameleon over USB

### Option A: ChameLab Desktop (easiest, no extra tools)

1. Open ChameLab Desktop (`dotnet run --project software/chamelab/ChameLab.Desktop -c Release`, or the installed app) and connect to the Chameleon over USB.
2. Open **Chameleon** → **Firmware** → **Install from file**.
3. Pick `firmware/objects/ultra-dfu-app.zip` (or `lite-dfu-app.zip`) and confirm.

The app switches the device into DFU mode, flashes the package via Nordic Secure DFU, waits for reboot and reconnects automatically.

### Option B: `nrfutil` (CLI)

1. Put the connected device into DFU mode:

   ```bash
   dotnet run --project software/chamelab/ChameLab.Cli -c Release -- -c "hw connect" -c "hw dfu"
   ```

   (Or unplug, hold button **B**, and plug USB back in — LEDs 4 and 5 blink.)

2. Flash the package:

   ```bash
   nrfutil device program --firmware firmware/objects/ultra-dfu-app.zip --traits nordicDfu
   ```

> **Note:** `firmware/flash-dfu-app.sh` is Linux-only (`lsusb` + `nrfutil`) and references a legacy `enter_dfu.py` path (the script is at `firmware/tools/enter_dfu.py`). Prefer Option A, Option B, or Option C.

### Option C: Headless CLI without `nrfutil` (.NET 10+ SDK)

`ChameLab.Core` and `ChameLab.Serial` include a pure-C# Nordic Secure DFU USB flasher (`FirmwareUpdateService` + `SerialDfuLocator`). With the .NET 10+ SDK you can run a small file-based C# script outside the repo (or in a temp directory) referencing `software/chamelab/ChameLab.Serial/ChameLab.Serial.csproj`:

```csharp
#:project <path-to-repo>/software/chamelab/ChameLab.Serial/ChameLab.Serial.csproj
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;
using ChameLab.Transport.Serial;

var package = DfuPackage.Parse(File.ReadAllBytes(args[0]));
var locator = new SerialDfuLocator();
var device = new DeviceService([new SerialScanner()]);
var updater = new FirmwareUpdateService(device, [locator]);
var found = (await device.ScanAsync(TransportKind.Serial)).FirstOrDefault();
if (found is not null) await device.ConnectAsync(found);
var target = found is not null ? updater.PrepareTarget() : locator.Prepare(null);
await updater.InstallAsync(package, target, new Progress<FirmwareUpdateProgress>(
    p => Console.WriteLine(p.Step == FirmwareUpdateStep.Flashing ? $"{p.Step} {p.Fraction:P0}" : $"{p.Step}")));
Console.WriteLine($"Done: {device.DeviceInfo?.ModelName} {device.DeviceInfo?.FullFirmwareVersion} ({device.DeviceInfo?.GitVersion})");
await device.DisconnectAsync();
```

Run it with `dotnet run flash.cs -- firmware/objects/ultra-dfu-app.zip` (adjust the `#:project` path to your checkout).

## 3. Verify the flashed firmware

```bash
dotnet run --project software/chamelab/ChameLab.Cli -c Release -- -c "hw connect" -c "hw version"
```

Confirm that the reported version matches `firmware/VERSION` (and includes `-dirty` when built from uncommitted changes in `firmware/`).
