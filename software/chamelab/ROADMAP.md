# Roadmap: parity with ChameleonUltraGUI (Flutter)

What ChameLab is missing compared with the original app,
[GameTec-live/ChameleonUltraGUI](https://github.com/GameTec-live/ChameleonUltraGUI).
Checked against its source (commit `7311c54`, 2026-10-24). Tick `[x]` as things land,
and drop finished items over time.

## High priority

### MIFARE Classic key recovery
Needs a crapto1 port or the native library in `software/src`.
- [ ] Darkside, nested, static nested, hardnested (nonce-collection progress)
- [ ] Backdoor / static encrypted nested
- [ ] Per-sector found-key marks, Skip / Resume recovery
- [ ] Partial data dump (only sectors with known keys)
- [ ] Additional dictionary and "Skip default dictionary" when checking keys
- [x] mfkey32 in the app: compute the key from collected nonces (currently only copies the `mfkey32v2` command)
- [ ] Save recovered keys: to file, to an existing dictionary, to a new one (including from a saved card)

### Sniffing
- [ ] HF sniffing: capture ISO14443-A, decode frames (reader/card), UID, protocol, AIDs, ATC/Amount, MFC auth groups, mfkey32/mfkey64 recovery from the trace, load `.trace`
- [ ] LF sniffing: capture 125 kHz ADC, waveform with zoom, modulation analysis (Manchester, ASK/NRZ, Biphase, FSK), clock detection, bit decoding, load `.bin`

### Editing slot data in place
- [ ] UID/SAK/ATQA/ATS and LF ID (Facility Code, Issue Level) in the slot itself (currently only by assigning a saved card)
- [ ] Export slot data: to file, to a new card, "Update saved card"
- [ ] Manual slot enable/disable (currently a slot is enabled only when a card is assigned)
- [ ] View Ultralight passwords from the detection log (`Mf0NtagGetDetectionLog`)

## Medium priority

### Library organization
- [ ] Folders for cards and dictionaries: create, nest, color, move, export/import a folder, delete with contents
- [ ] Dump comparison (Compare / Difference)

### Dump editor
- [ ] Value blocks: decode value, Inc/Dec (`Mf1ManipulateValueBlock` is declared but unused)
- [ ] Decode access conditions per block (currently only a valid / INVALID mark)
- [ ] BCC check
- [ ] Export contents as ASCII
- [ ] NDEF editor (records, add/remove, byte counter)

### Reading cards
- [ ] Continuous scan
- [ ] Override card type (manual type instead of auto-detection)
- [ ] Show backdoor support and read Gen1 via backdoor without keys
- [ ] Warning that Chameleon Lite cannot read cards

### Writing cards
- [ ] Vanity SAK (SAK in block 0 + 0x80)
- [ ] Write lock bytes and counter for Ultralight C (`Mf0UlcSetKey` unused)
- [ ] Warnings: OTP cards, "keep the device still", "Write again", "Continue anyway" on partial failure

### Tools
- [ ] Dictionary download: Proxmark3 dictionaries (MFC, T55xx, MFULC, MFP) and Flipper
- [ ] T55XX password cleaner: brute-force the password from a dictionary and set a new one

### Firmware and connection
- [ ] "Enter DFU mode" button and unconditional "Flash latest FW" (currently Install latest only when an update exists)
- [ ] Detect a device already in DFU (corrupt firmware) and offer to reflash
- [ ] Warning about an outdated firmware protocol
- [ ] Manual connection by serial port name (currently manual entry is TCP only)
- [ ] Auto scan devices / Auto connect first found device
- [ ] BLE connect screen with hints (PIN 123456, slow first connection, remove from the Bluetooth list)

## Low priority
- [ ] Battery voltage in the UI (the client already reads `voltage` but does not show it)
- [ ] Emulated (demo) device — try the app without hardware
- [ ] System / Light / Dark theme (currently only a Light/Dark toggle; `AppearanceService` already stores `Default`)
- [ ] Sidebar mode (Expand / Retract / Auto)
- [ ] Localization (~20 languages) and "Help translate"
- [ ] Logs: production logging, view, copy, export, clear
- [ ] Debug page
- [ ] App changelog with recent commits (currently release notes exist only for firmware)
- [ ] About: contributors, Open Collective, licenses
- [ ] Export/import settings via QR codes (mobile only in the original)
- [ ] "Update card data?" prompt when the UID/SAK/ATQA of a card changes

## Not counted
Present in the original's code but unused in its UI — not included in parity:
`scanEM4X05`, `hf14a4EmvScan`, `hf14a4ReaderApdu`, `hf14a4AddStaticResponse`,
`adcGenericRead`, `writeT55XX`, `ioProxDecodeRaw`, `manipulateValueBlock`.
Mifare Classic Gen4 is marked WIP in the original.
