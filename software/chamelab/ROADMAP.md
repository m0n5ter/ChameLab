# Roadmap: parity with ChameleonUltraGUI (Flutter)

What ChameLab is missing compared with the original app,
[GameTec-live/ChameleonUltraGUI](https://github.com/GameTec-live/ChameleonUltraGUI).
Checked against its source (commit `2bafc00`, 2026-11-09, release 1.4); ChameLab state as of v1.4.35.
Tick `[x]` as things land, and drop finished items over time.

## Done
- Backdoor / static encrypted nested (FM11RF08S and compatible cards), managed solver on all platforms
- Darkside, Nested, Static nested, mfkey32 and mfkey64 key recovery; Hardnested on desktop, Android, iOS and the web
- Per-sector key grid with found-key marks; stopping a dump keeps what was read and
  a partial dump can be continued
- Dictionary selection for dumps, shared with target-card key recovery; recovered keys
  kept in the "Known card keys" dictionary; Save keys menu (clipboard, a dictionary, a new one, `.dic` file)
- Card folders
- HF sniffing: ISO14443-A frames from both sides, labelled (anticollision, MIFARE Classic, Ultralight, ISO14443-4 APDUs, AIDs), mfkey32 / mfkey64 key recovery, Proxmark3 `.trace` load / save
- LF sniffing: 125 kHz capture, waveform with zoom, modulation guess, clock detection, Manchester decoding, `.bin` load / save
- Card type override in the card editor
- Writing to Gen1a, Gen2 and Gen3 magic cards, T55xx with a password
- Slot editing in place, saving a slot to Cards, manual slot enable/disable, Ultralight password detection
- Dump editor: BCC check, access bits valid / invalid mark, NDEF editor
- T55XX password recovery from a dictionary and setting a new password
- Jablotron (read, emulate, write to T55xx)
- Battery level and firmware battery condition; NFC field generator as a button action
- Auto-detected devices (USB and BLE), manual TCP bridge

## High priority

### MIFARE Classic
- [ ] Firmware batch key checks (`Mf1CheckKeysOfSectors`, 2012) in dictionary checks of the app
  (only the CLI uses it; the app checks block by block with `Mf1CheckKeysOnBlock`)
- [ ] Value block tool: decode value, Inc / Dec / Restore / Transfer on a live card,
  with key recovery when keys are missing (CLI has `hf mf value`; `Mf1ManipulateValueBlock`)
- [ ] Gen4 Ultimate (GTU) magic card configuration: UID length, SAK/ATQA/ATS, protocol, Ultralight mode, password

### Tools
- [ ] Raw T55xx block editor: read and write single blocks, with a password
- [ ] Raw command tools (Advanced): ISO14443-A frames (`Hf14aRaw`) and arbitrary device commands
- [ ] Active authentication trace (`Hf14aAuthTrace`, 2017; our firmware has it): block, key A/B, key, timeout;
  frames, nonces, recovery and `.trace` export through the HF trace views
- [ ] LF capture from the ADC (`AdcGenericRead`, 3009) as a second capture source, sample range and hex view

## Medium priority

### LF cards
- [ ] ioProx built from fields (version, facility code, card number) when creating and editing a card
- [ ] HID Prox format names in the editor and details (currently only the number), including HID ACTProx (format 42)

### Library organization
- [ ] Folders for dictionaries; export/import a folder
- [ ] Dump comparison (Compare / Difference)

### Dump editor
- [ ] Decode access conditions per block (currently only a valid / INVALID mark)
- [ ] Export contents as ASCII

### Reading cards
- [ ] Continuous scan
- [ ] Read Gen1 via backdoor without keys
- [ ] Warning that Chameleon Lite cannot read cards

### Writing cards
- [ ] Vanity SAK (SAK in block 0 + 0x80)
- [ ] Write lock bytes and counter for Ultralight C (`Mf0UlcSetKey` unused)
- [ ] Warnings: OTP cards, "keep the device still", "Write again", "Continue anyway" on partial failure

### Dictionaries
- [ ] Dictionary download: Proxmark3 dictionaries (MFC, T55xx, MFULC, MFP) and Flipper

### Firmware and connection
- [ ] "Enter DFU mode" button and unconditional "Flash latest FW" (currently Install latest only when an update exists)
- [ ] Warning about an outdated firmware protocol
- [ ] Manual connection by serial port name (currently manual entry is TCP only)

### Builds
- [ ] Linux ARM64 desktop build

## Low priority
- [ ] Extensions: JSON plugins with access to the device, saved data, key recovery and settings
- [ ] Emulated (demo) device — try the app without hardware
- [ ] System / Light / Dark theme (currently only a Light/Dark toggle)
- [ ] Sidebar mode (Expand / Retract / Auto)
- [ ] Localization (~20 languages) and "Help translate" — 10 languages so far: en, de, es, fr, it, ja, ko, pt, ru, zh-Hans
- [ ] Logs: production logging, view, copy, export, clear
- [ ] Debug page
- [ ] App changelog in the app (currently release notes exist only for firmware)
- [ ] About: contributors, Open Collective, licenses
- [ ] Export/import settings via QR codes (mobile only in the original)
- [ ] "Update card data?" prompt when the UID/SAK/ATQA of a card changes
- [ ] Hardnested speed and memory: the original sped up its native solver in 1.4; compare with ours on a benchmark

## Not counted
Present in the original's code but unused in its UI — not included in parity:
`scanEM4X05`, `hf14a4EmvScan`, `hf14a4ReaderApdu`, `hf14a4AddStaticResponse`, `ioProxDecodeRaw`.
