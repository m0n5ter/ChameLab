# Change Log
All notable changes to this project will be documented in this file.
This project uses the changelog in accordance with [keepchangelog](http://keepachangelog.com/). Please use this to write notable changes, which is not the same as git commit log...

Releases are tagged with the ChameLab version, which goes up with every release (v1.0.7 on).
The firmware has its own version in `firmware/VERSION` (v2.3.2-cl on), bumped only when the firmware changes.

Each release (v1.4.35 on) opens with `### Summary`, up to three of its most important changes, followed by `### Details`, the full list. The release announcements in Telegram and Discord and the download page show the summary with a link to this file.

## [unreleased][unreleased]
 - ChameLab: the browser version installs firmware over Web Serial and Web Bluetooth. The first time, the browser asks to pick the Chameleon's bootloader in its device list (Select bootloader in the update dialog); later updates over USB find it on their own

## [v1.5.40][2026-11-11] · firmware v2.3.15-cl
### Summary
 - ChameLab: Share this device lets someone use your Chameleon from another computer over the internet: they connect with a code such as RU1-ABCD-1234
 - ChameLab: faster key recovery: Darkside up to 20 times, static encrypted nested about 3 times, nested about 1.4 times
 - ChameLab: static encrypted nested reads default-key cards without cryptanalysis and no longer runs a long fruitless search on ordinary backdoored cards

### Details
 - ChameLab: Share this device on the Connect page (desktop app) shares the selected Chameleon through the fastest of five relay servers (Turkey, Bulgaria, Germany, Russia, USA) and shows a code such as RU1-ABCD-1234. Connect to a shared device with that code, on another computer or an Android phone, works with the Chameleon as if it were plugged in. While sharing, the Connect page shows only the shared Chameleon, the code, who is connected (user@computer) and the traffic both ways. One person at a time; they can leave and connect again with the same code, and Stop sharing ends it. Connections to the relays are encrypted, with each server's certificate built into the app; the browser version can't connect to shared devices
 - ChameLab: Static encrypted nested first checks known and factory keys, so default-key cards finish without cryptanalytic recovery; a second capture checks that encrypted nonces are static before calculating candidates, avoiding a long fruitless search on ordinary backdoored cards
 - ChameLab: nested, static encrypted nested and Darkside recovery share the first Crypto1 candidates, run independent groups in parallel and check Darkside prefixes in batches: on a desktop Darkside is 7 to 21 times faster, static encrypted nested 3 to 3.7 times, nested 1.4 to 1.5 times
 - ChameLab: the Connect buttons have a new icon, the big Connect button no longer stretches across the card, and single-line text boxes keep their text vertically centered
 - ChameLab: Load and Save in HF sniffing no longer change Capture to Listening; the label changes only during a capture
 - ChameLab: HF sniffing shows each frame's time from capture start and the pause after the previous frame. Firmware v2.3.15-cl records frame boundaries with a hardware timer; Proxmark3 `.trace` files retain timing on load and save, and older firmware still works without measured times

## [v1.4.39][2026-11-10] · firmware v2.3.14-cl
### Summary
 - ChameLab: much faster MIFARE Classic dictionary key recovery: the firmware checks keys in batches instead of one authentication per key, most noticeably on a phone over Bluetooth
 - ChameLab: Try keys has key search options: search method, start sector and sector order
 - ChameLab: T55XX password recovery by dictionary no longer stops early when a read misses the tag

### Details
 - ChameLab: recovering MIFARE Classic keys by a dictionary checks the dictionary in batches with one firmware command each (8 keys over USB, 32 over Bluetooth) instead of one authentication per key, much faster, most on a phone over Bluetooth; when a batch opens a sector with key A, the firmware reads key B straight from the sector trailer. The dictionary is checked once: after the batch pass, the per-sector search only retries keys that opened other sectors. Found keys and progress show while a batch runs, and a failed batch falls back to checking key by key. Firmware without the batch command still checks key by key
 - ChameLab: a settings button next to Try keys opens the key search options, remembered between runs: Search method (One key at a time, Batches across all sectors, Batches in one sector), Start sector (First sector or the selected one) and Sector order (Sequential or Random)
 - ChameLab: T55XX password recovery by a dictionary no longer aborts on a wrong key when a read misses the tag: a read of the original ID already proves the write was ignored, and the check that the tag is intact retries up to three reads
 - ChameLab: in the T55XX password settings the Keep hint lines up with the Clear hint

## [v1.4.38][2026-11-10] · firmware v2.3.14-cl
### Summary
 - Firmware: T55XX writes no longer fail or corrupt the tag when the commands come slowly, such as over Bluetooth
 - ChameLab for Android: a copy installed from Google Play is updated by Google Play

### Details
 - Firmware: a T55XX block write powers the tag up for 10 ms instead of 1 ms before the first command, since the field is off between writes and a tag left long enough (over Bluetooth or a slow USB host) discharged and missed the command
 - ChameLab: a copy installed from Google Play no longer checks GitHub for app updates, Google Play updates it; the CI also builds the Google Play bundle (.aab)

## [v1.4.37][2026-11-10] · firmware v2.3.13-cl
### Summary
 - ChameLab: faster hardnested key recovery
 - ChameLab: Known card keys in the T55XX dictionary picker, and All/None shortcuts in the dictionary pickers
 - Firmware: a button press made during the wake-up animation is no longer lost

### Details
 - ChameLab: fully managed, portable hardnested optimizations with parallel page reduction and candidate filtering, cached partial sums and improved guess ordering; [comparison with ChameleonUltraGUI 1.4](software/benchmarks/hardnested/GUI-1.4-OPTIMIZED.md)
 - ChameLab: the T55XX password dictionaries (Tools and the card editor's T55XX write) offer Known card keys, the passwords learned for every tag, selectable like any dictionary; the scanned tag's own learned password is tried first only while Known card keys are checked. Both pickers list Known card keys, the built-in keys, a divider, then the dictionaries
 - ChameLab: the dictionary pickers (MIFARE key recovery and T55XX passwords) have All and None shortcuts next to the header
 - ChameLab: when no T55XX password matches, the Found and New rows read Not found and Not set in red instead of a sentence; on success both show in green
 - ChameLab: in Cards, a scanned card that is already saved no longer offers Save/Discard and no longer asks before switching to another card
 - Firmware: a second button press within about a second of a button wake-up is no longer lost while the power-on animation plays

## [v1.4.36][2026-11-09] · firmware v2.3.12-cl
### Summary
 - ChameLab: T55XX password change in Tools and in the card editor's T55XX write: a known password or a dictionary attack, then Keep the tag's password, Clear it to the default or Set a new one; the password is remembered for the tag in Known card keys
 - ChameLab: key dictionaries hold keys of 4 to 16 bytes, also of different lengths in one dictionary, and the card editor's Save keys menu puts a card's known keys into a dictionary or a `.dic` file
 - ChameLab for Android: firmware update over USB

### Details
 - ChameLab: on a short window the Cards and Dictionaries lists and the dictionary keys scroll under the page title: the list (or the key editor) fills the page while it can, and once the window is too short for it the whole area under the title scrolls
 - ChameLab: long explanatory captions (Tools, the key editor, Add card, Settings, device settings) shrink to one line when the page is short of height, such as a phone in landscape or with the keyboard up; the tooltip shows the whole text
 - ChameLab: when the page is short of height, the gaps between its blocks and the padding above and below their content are smaller
 - ChameLab: the battery and operating mode cards on the Chameleon page go one per row before they get too narrow for their content
 - ChameLab: key dictionaries hold keys of 4 to 16 bytes, also of different lengths in one dictionary: in the hex editor each row is as long as its key, typing after the last byte lengthens it, Backspace and Insert delete and insert bytes, and pasting several lines adds a key per line. The list filters by key length; MIFARE Classic recovery takes the 6-byte keys of every dictionary, T55XX recovery the 4-byte ones
 - ChameLab: pasting into a hex editor from a phone keyboard's clipboard (or with Win+V) no longer crashes the app when the paste adds rows, and a pasted key list keeps one key per line; a paste with `#` comment lines is no longer ignored
 - ChameLab: the hex editor shades every other row (a row wrapped over two lines counts as one); a key that fills whole lines no longer trails an empty line for its next byte, which appears once the caret moves past the last byte; the current row is highlighted only while the editor has focus
 - ChameLab: scrolling no longer judders while an app update is available: the pulsing update icon in the top bar used to redraw the whole window at the display's rate (120 times a second on some phones); it now steps 10 times a second
 - ChameLab: a scrolling long name (a card in a slot, the active slot) no longer keeps the app redrawing while it rests at either end or is scrolled out of view
 - ChameLab: a Save keys menu in the card editor puts the card's known keys (MIFARE Classic keys A and B without the unknown ones, the Ultralight EV1 / NTAG password) on the clipboard, into one of the dictionaries, into a new dictionary named after the card or into a `.dic` file, one hex key per line; a dictionary only gets the keys it does not have yet
 - ChameLab: the dictionary pickers (keys for a dump, T55XX passwords) show each dictionary on a compact line: the switch on the left (a click anywhere on the row flips it), a square of its color, the name sliding when it is too long and the number of keys in a small frame on the right; the Save keys menu lists the dictionaries the same way
 - ChameLab: menu buttons in the card editor (Export, Save keys) shown as icons only when space is short are padded, so the icon no longer touches the frame
 - ChameLab: Known card keys keep every learned card with its type: MIFARE Classic keys, the NTAG / Ultralight password and now the T55XX password, saved by the ID the tag broadcasts after a T55XX password recovery or write and tried first the next time that tag is written. The editor lists all of them with the type next to the UID, filters by card type (as well as by UID, card name or type in the search), edits a password card's 4-byte password and lets a card learned before types were kept be given one; the stored file is in a new format, older backups still restore, but a backup made now cannot be restored by an older version
 - ChameLab: the T55XX password panel in Tools is reworked. Current key: Know (a 4-byte password) or Dictionary attack (a Key dictionaries button with the number of keys in a small frame). New key: Keep, Clear (writes the default 20206666) or Set (a password typed below). Keep leaves the tag's password and protection as they were, and an unprotected tag stays unprotected
 - ChameLab: T55XX in Tools: Start shows only while a Chameleon is connected and turns into Cancel in place (Esc cancels) with the progress beside it; the UID, the matched and the new key show as spaced hex, and a Save keys menu saves the recovered password to the clipboard, a dictionary, a new dictionary or a `.dic` file
 - ChameLab: writing to a T55xx tag from the card editor has the same password settings as Tools: a known password or a dictionary attack, then Keep, Clear or Set
 - ChameLab for Android: firmware update over USB (a device connected over USB could not be flashed)
 - ChameLab for Android: leaving with a double Back closes the app and disconnects the device; from Android 12 the app used to stay in the background with the device connected
 - ChameLab for Android: after coming back from the background, the room kept for a keyboard that is gone is released

## [v1.4.35][2026-11-05] · firmware v2.3.12-cl
### Summary
 - CLI: the command line client is rewritten in C# on the same protocol and key recovery code as the app; it comes as a single self-contained program for Windows, macOS and Linux, with no Python to install
 - ChameLab: a new logo, in the app and on the website
 - ChameLab for Android: connecting over USB works (the device used to restart a second after connecting)

### Details
 - CLI: the command line client is rewritten in C#: it shares the protocol, card models and key recovery (darkside, nested, static nested, hardnested, Ultralight C) with the app instead of the Python client and its native solvers. Every command, flag and file format of the Python client is kept, with history and Tab completion, repeated `-c` / `--command`, Ctrl+C to stop a command and meaningful exit codes. The release carries it as `client-windows.zip`, `client-linux.zip` and `client-macos.zip`, a self-contained `chamelab` program that needs neither Python nor .NET
 - CLI: the development tools are in C# too (hardnested table import and code generation, benchmarks, the MIFARE Classic 4K reader, release packaging and announcements); the DFU helper scripts moved to `firmware/tools`
 - Website: the home page says the CLI is written in C# and shares the app's code
 - Website: the download page shows a release's summary with a link to the full changelog; the Telegram and Discord announcements do the same
 - ChameLab: a new logo, a geometric redraw of the chameleon with the gear: the app icon, the Android splash screen, the Windows window and .exe icon and the top bar mark (in place of the ChameleonUltra picture)
 - Website: the header shows the new logo in place of the "C" badge, without the dot after ChameLab; the website and the web app (app/ and dev/) have the new favicon (the web app had the browser's default one)
 - Website: the home page headline has no dot, and "The lab for your" is set smaller above "Chameleon"
 - Website: the home page hero shows the ChameLab logo in place of the large ChameleonUltra photo
 - Website: headings no longer end with a period
 - Website: on the download page the web app button comes first, across the whole row, and the firmware packages go two to a row: ChameleonUltra above, ChameleonLite below, and the two SWD firmware binaries share the whole row; on a narrow screen they go one to a row. The command line client comes before the firmware
 - ChameLab: connecting over USB on Android works: the device restarted a second after connecting (the app showed an error for a moment and went back to Connect), because the app sent the next command before it had read the reply to the previous one; over Android USB it now waits for each reply first
 - Firmware: replies over USB wait in a queue until the host reads them; a reply made while the previous one was still going out used to overwrite it and restart the device
 - ChameLab: on narrow screens the Rescan button on Connect and the actions of the Chameleon danger zone are laid out like settings: the title gets the full width, the description and the button share the line under it
 - ChameLab: shorter button labels: Add and Choose in the slot editor (were Add saved / Choose saved), Save in the slot editor and the dictionary import (were Save slot / Save dictionary)
 - ChameLab: the Cancel / Discard / Save prompts about unsaved changes have icons, and on a narrow screen the buttons fall back to icons one at a time, Cancel first and Save last
 - ChameLab: Settings has a link to the website, first in the row of community links; short of room, the links drop their labels from the end
 - ChameLab: no empty space at the bottom of Settings and Chameleon
 - ChameLab: Tools shows the two sniffing tiles one under the other when they do not fit side by side untrimmed
 - ChameLab: Settings: the website and community links drop their labels by priority (MAX first, then Discord, Telegram, the website last); the backup buttons are just Export and Import

## [v1.3.34][2026-11-05] · firmware v2.3.11-cl
 - ChameLab: smoother scrolling of long key lists (thousands of keys) and card memory on phones: the hex editor keeps the lines it has drawn in small strips that scrolling only moves, adding one strip at a time as the view nears it; the bytes and row numbers are drawn as batched glyphs, one run per color for each strip, also with a proportional font (Android falls back to Roboto, where every byte was laid out as text of its own and the renderer's memory use made scrolling hitch a few times a second), and the blinking caret no longer redraws the text
 - ChameLab: no more freezes of a tenth of a second every few seconds while scrolling on Android: devices are looked for only while the Connect page is shown (the scan used to keep running on every page while nothing was connected, and each nearby Bluetooth device's advertisements piled up objects that every garbage collection had to check with Android), and the runtime collects its young objects less often
 - ChameLab: hex, keys and code use the bundled Cascadia Mono font on every platform (Android showed them in Roboto, so columns did not line up)
 - Website: the downloads have a page of their own, `download/`; the old `#stable` and `#dev` links lead there, and the app's update button opens it
 - Website: the home page shows the latest app version next to the app, as it does for the firmware

## [v1.3.33][2026-11-05] · firmware v2.3.11-cl
 - ChameLab: Settings picks the update channel: Stable, or Development, which also offers the dev pre-release when it is newer than the latest release. The quiet update check repeats every four hours and keeps a found update when a later check fails, and the top bar update icon pulses while an update is available
 - ChameLab: Cards > Import file and Settings > Import backup read the ChameleonUltraGUI settings export, folder export and desktop shared_preferences.json: nested folders become one-level paths ("Work / Doors"), empty folders are created, and cards already in the library are filed into their folders instead of duplicated. The backup import also brings the dictionaries not yet here
 - ChameLab for Android: the APK also carries 32-bit ARM (armeabi-v7a), so it installs on devices with a 32-bit system, common on Android 8.x
 - ChameLab: the About card links to the community: Discord, plus Telegram and MAX in Russian
 - Website: a new slogan, the community links in the footer and a smaller device picture
 - Website: the Stable release and Development build offer a button per platform with its icon, the one for this device highlighted, and show their part of the changelog; each has its own web app button: the stable web app at `app/` (the top button), the development one at `dev/`. Releases carry the web app as `chamelab-<version>-web.zip`
 - Website: picking a language reloads the page, also when its address has an #anchor

## [v1.3.32][2026-11-04] · firmware v2.3.11-cl
 - ChameLab: the app is translated into German, Spanish, French, Italian, Portuguese, Russian, Chinese (Simplified), Japanese and Korean; the language is picked in Settings and applies right away, without a restart: the window is rebuilt in the new language while the open page, the connection and running operations carry on. The web app bundles the Chinese, Japanese and Korean glyphs it needs and now loads the chosen translation
 - Website: the download page and the guide are available in the same ten languages, picked from the menu or the browser's language
 - ChameLab: Hardnested now uses a shared, fully managed C# port of current Proxmark3 on desktop, Android, iOS and the web; compact state tables reduce memory use and remove the separate native hardnested build dependencies
 - ChameLab: sorting dictionaries and cards by name is now natural: numbers in names compare by value, so A1, A2, A11, A21 instead of A1, A11, A2, A21
 - Firmware: write EM410x IDs onto EM4205 / EM4305 tags (new command 3034): blocks 5–6 get the ID and block 4 the EM410x configuration (Manchester, RF/64, as Proxmark3 writes it); the result is read back with the EM410x reader and a mismatch is reported as an error. Raw word write and read (3035, 3036) take an optional password for tags that require login
 - Firmware: EM4x05 scan now follows the EM4205 / EM4305 datasheet: correct command framing and parity, UID from block 1 and configuration from block 4, read-login and write-login bits from their real positions, and an optional LOGIN password. The previous framing did not match the chip
 - ChameLab: EM410x cards can be cloned onto an EM4305 instead of a T55xx: pick the chip in the writer; the current password field is sent as LOGIN when the tag is write-protected
 - CLI: `lf em 410x write --em4305 [--pwd]`, `lf em 4x05 rdbl` and `lf em 4x05 wrbl`; `lf em 4x05 read` takes `--pwd` and decodes the configuration word

## [v1.2.31][2026-11-03] · firmware v2.3.10-cl
 - ChameLab: Write another writes the same card onto the next tag right away, with the same options, instead of returning to the idle writer; on a Gen2 card whose previous target used the factory key in every sector, the next target is assumed to use it too, so no key recovery is needed first
 - ChameLab: the Write and Write another buttons are brand-blue outline buttons, and the writer's Cancel is a danger button while a write runs
 - ChameLab: NTAG / Ultralight EV1 passwords are remembered per UID in the known card keys (a new `ntagPasswords` section of known-card-keys.json), after a card accepts one on read or write or is given a new one; a read with no password entered presents the remembered one, and a write falls back to it when the card refuses the entered password

## [v1.2.30][2026-11-03] · firmware v2.3.10-cl
 - ChameLab: smooth touch scrolling of the card editor and its memory on phones: the memory editor no longer redraws every line on each frame while the page scrolls it into or out of view

## [v1.2.29][2026-11-03] · firmware v2.3.10-cl
 - ChameLab: the dump button of an Ultralight / NTAG card reads "Read" instead of "Try keys", since these cards are read without key dictionaries
 - ChameLab: an Ultralight EV1 / NTAG dump sends the entered password only when the card's configuration needs it (read-protected pages, protected configuration or a protected NFC counter), so an open card doesn't spend its AUTHLIM attempts on a wrong password
 - ChameLab: the memory editor shows Ultralight / NTAG memory four pages (16 bytes) to a line when there is room, then two or one, so dumps are shorter and NDEF text reads as a whole in the ASCII column
 - ChameLab: the Ultralight / NTAG memory section shows the page strip from the scan on, with the password beside Read and both gone once every page is read; pages the card refuses show as No key, pages lost when the card leaves the field as Error. Password protection moved below the memory, with its settings disabled while protection is off, From page and the attempt limit (now a number box) on a line of their own, and no scroll jump when toggling it
 - ChameLab: the Ultralight / NTAG writer asks for the "Current card password" and sets the target's password settings with the same controls as the card's Password protection, starting from the card's and editable without changing it, or keeps the target's own settings; the card editor's Write button is brand blue
 - ChameLab: the NDEF editor moved into the memory section, which switches between Hex and NDEF for Ultralight / NTAG cards. Records are edited in place and every valid change goes straight into the memory (no Edit / Apply); records are added from the Add record menu, show the kind's icon, keep their kind and are dragged into place by any spot that isn't an input; the bytes they take and what is left on the card are counted, too many in red
 - ChameLab: NDEF records for a phone number, an email, a text message (SMS), a location and a contact (vCard), each with its own fields; links of these kinds read from a card open as them
 - ChameLab: a MIFARE Classic or Ultralight / NTAG card entered manually starts with the memory of a factory-blank card of its type (all blocks and keys known, block 0 / the UID pages following the identity as it is typed), and its memory section has no Read, Try keys or key recovery controls, as there is no card to read
 - ChameLab: Tab moves through every NDEF record's fields in turn instead of skipping the whole list to Add record; the memory heading and its page count share a baseline, with less space above the page strip

## [v1.1.28][2026-11-02] · firmware v2.3.10-cl
 - ChameLab: faster Static encrypted nested recovery: candidate calculation is about 1.4× faster and RF08S A/B seed filtering is 7–7.5× faster in local Windows Release benchmarks. Candidate sets remain identical to the original solver; reproducible benchmarks and regression checks are included
 - Website: a user guide with screenshots covers connecting, slots, card reading, key recovery, dictionaries, sniffing and device settings

## [v1.1.27][2026-11-02] · firmware v2.3.10-cl
 - Firmware: preserve multi-card LF lists when updating from firmware v2.3.6-cl / v2.3.7-cl: read both record formats 04 and 05, keeping every card and its order while ignoring obsolete timing settings. Lists in format 04 are upgraded to 05 on the next slot save. This also recovers lists ignored by v2.3.8-cl if their flash records have not been overwritten
 - Firmware: opening the USB serial port (DTR) stops Bluetooth advertising and disconnects any active BLE client for the whole USB session, preventing BLE radio events from interrupting LF timing. Closing the port or unplugging USB restores advertising; connecting USB only for charging keeps Bluetooth available
 - Firmware: Electra scans wait for the full 13-byte ID instead of returning its 5-byte EM410x prefix. EM410x and Electra decoding also reacquires the Manchester phase after an invalid interval, improving reads that start between frames
 - Firmware: writing a T5577 stores its password and data before enabling password protection in the configuration block. This fixes writes to blank tags that previously enabled protection before their password was set and keeps protected tags rewritable with the chosen password

## [v1.1.26][2026-11-02] · firmware v2.3.8-cl
 - Firmware v2.3.8-cl / ChameLab: multi-card LF emulates one steady card per reader-field appearance and advances to the next card on the next tap, wrapping back to the first after the last card. The next position survives sleep; a battery pull or switching slots restarts at the first card
 - ChameLab: the multi-card LF editor no longer has time-per-card or silence settings; cycle commands 5907–5909 no longer carry those fields. Update the app and firmware together. Updating the firmware drops lists saved by earlier firmware, while keeping each slot's own card; add the extra cards again after updating
 - CLI / source checkout: `Read-Mfc4k.ps1` runs MIFARE Classic 4K key recovery and reading through the CLI, records recovery, reading and total times in `report.json`, and saves a partial dump after every sector

## [v1.1.25][2026-11-01] · firmware v2.3.7-cl
 - ChameLab: faster managed Nested recovery with reusable Crypto1 workspaces, linear state grouping, table-based key extraction and radix sorting; desktop and mobile use available CPU cores for independent nonce candidates
 - ChameLab: Nested compares measured recovery and authentication times when choosing whether to try known keys before a fresh attack, avoiding long key-reuse passes when recovery is faster
 - ChameLab web: Nested, Static nested, Static encrypted nested, Darkside and mfkey32 calculations run in a reusable Web Worker, keeping the interface responsive and allowing Stop to interrupt calculation immediately
 - ChameLab web: Release publishing enables managed WebAssembly AOT to speed up key recovery; the algorithms remain C#, with a larger download and longer publish time

## [v1.1.24][2026-11-01] · firmware v2.3.7-cl
 - ChameLab: Nested, Static nested and Hardnested try keys already found for the card before attacking each unknown sector key, for both A and B; keys with more successful uses are tried first. A final pass checks newly found keys against earlier unresolved sectors without repeating rejected combinations; RF errors are retried rather than remembered as wrong keys
 - ChameLab: confirmed keys are saved to Known card keys immediately during recovery, including when recovery is later stopped
 - Firmware v2.3.7-cl: ISO14443-4 (T=CL) emulation no longer truncates reader commands of 32 bytes or more (e.g. an EMV GPO with a full PDOL); the received bit count wrapped at 256
 - Firmware v2.3.7-cl: BLE advertising is no longer restarted while a client is connected; it resumes on disconnect

## [v1.1.23][2026-10-31] · firmware v2.3.6-cl
 - Firmware v2.3.6-cl / ChameLab: multi-card LF works with every emulated LF type (EM410x, Electra, HID Prox, ioProx, Viking, PAC, Jablotron, IDTECK, Pyramid), mixed freely in one list; a burst lasts 0.16–0.65 s depending on the type. The cycle commands are replaced by 5907–5909, which carry each card's type and the silence; lists saved by earlier firmware are dropped (each slot keeps its own card), and earlier firmware shows the single-card LF editor. Uploading a card of any LF type now stops emulation meanwhile and clears the slot's list
 - Firmware v2.3.6-cl / ChameLab: connecting and opening Slots no longer runs the slot LED animation several times: a new command reads the multi-card LF lists of all slots at once, without switching the active slot (older firmware keeps the previous way)
 - ChameLab: Hardnested recovery on Android, iOS and the web; local native/WebAssembly calculation with progress, cancellation and a bounded solver memory budget
 - Firmware v2.3.6-cl: the sleep timeout now applies to every restart of the sleep timer, not only after a button wakeup: button presses, end of HF / LF emulation, BLE disconnect and USB power loss no longer cut the remaining awake time to a fixed 3–4 seconds
 - Firmware v2.3.6-cl: multi-card LF silence between cards now looks like the card was taken away: the antenna is detuned (shorted) and the field LED is off for the whole gap, so readers that wait for the previous card to be removed see the next one
 - ChameLab: the Sleep timeout setting is limited to the 5–60 seconds the firmware accepts
 - ChameLab: Cards has a New folder button beside Add card (it opens the new folder with its name ready to edit); in the navigation pane New folder now comes first too. An open folder's Rename and Delete have labels too. These buttons drop their labels only when the list is too narrow, Add card last
 - ChameLab: number fields with − / + buttons use slimmer buttons at phone width and in narrow setting rows, so the value fits

## [v1.1.22][2026-10-30] · firmware v2.3.4-cl
 - Website and README: describe multi-card LF (up to eight EM410x cards cycling in one slot, unique to ChameLab) and stopping any long operation; HF / LF sniffing is no longer listed as on the roadmap
 - ChameLab: switching pages is instant: the pages are built in the background while the app is idle, and then only shown or hidden (it used to be rebuilt and cross-faded every time, about half a second on phones). Pages keep their scroll position between visits
 - Firmware v2.3.4-cl: an LF slot can cycle through up to eight EM410x cards, with configurable time per card and 0–3 seconds of silence between cards in 0.1-second steps. Lists and timing settings persist across restarts and follow slots when swapped; existing single-card slots remain compatible.

## [v1.0.21][2026-10-29] · firmware v2.3.3-cl
 - ChameLab: the memory editor's Clear sector button uses danger styling and a delete icon
 - ChameLab: scanning a new card no longer probes MIFARE Classic recovery methods; Try keys and Check recovery methods are offered first, with attacks shown after an explicit check
 - ChameLab: mfkey32 no longer fails now and then with "The operation was canceled" when the nonces fit more than one key; it reports that no unique key was found. The Crapto1Sharp code, which carries the fix, is now part of ChameLab (ChameLab/Crypto)
 - ChameLab: new Tools page (in place of the old Sniff page) with Sniff HF and Sniff LF; each opens its own view with a Back button, like Add card. Its header (hint and one adaptive row of buttons) stays in place while the results scroll
 - ChameLab: Sniff HF: for 3–30 s the Chameleon answers a reader as the card in its active slot and records the reader's frames and its own answers. Every frame is labelled (REQA, anticollision, SELECT, SAK, AUTH, nonces, Ultralight commands, ISO14443-4 APDUs with AID names and status words), the encrypted part of a MIFARE Classic session is marked, and the card, AIDs and authentications are summed up. Frames have their own virtualized list, one line each when there is room (the meaning goes under the bytes otherwise); a range can be selected (Shift / Ctrl+click, or two taps on a phone) and copied. Reader keys are recovered in the app (mfkey32 from two tries on a block, mfkey64 from one the card accepted) and shown as a green badge. Captures load and save as Proxmark3 .trace files
 - ChameLab: messages no longer cover the Save / Discard bar or need closing. A failure shows under the page (the page ends above it, so nothing moves at the top), with Copy and close buttons; it goes once another operation starts or the page is left. Why edits could not be saved shows in the save bar next to Save. Confirmations are a short notice at the top that clears after 3 s and lets clicks through, and are left out where the result is already on screen (saving, duplicating, deleting)
 - ChameLab: Sniff LF: records 32 ms of the 125 kHz antenna signal with its levels, field gaps, a modulation and clock guess and a Manchester decode at a chosen RF/n clock, plain or inverted. The waveform draws from a min / max pyramid so its cost follows the width, zooms with the wheel, the slider or a pinch (touch screen or touchpad), pans by dragging, has an overview strip framing the part in view, a legend, and a cursor whose sample, time and level are shown under it. The samples are listed in a read-only hex view that draws only the rows in sight. Captures load and save as raw .bin files, also without a device

## [v1.0.20][2026-10-29] · firmware v2.3.3-cl
 - ChameLab: browsing card folders keeps the current card and its unsaved data in the editor without asking to Save / Discard; selecting another card still asks before replacing it
 - ChameLab: unsaved card and dictionary edits survive leaving and returning to their pages
 - ChameLab: Android connects over USB OTG for card scans, reads, writes and device commands; scan vibration works more reliably
 - ChameLab: Gen2 MIFARE Classic writing has a target-card key recovery panel, sector progress and an optional block 0 write; source-card keys can be tried against the target
 - ChameLab: dictionary selections are shared between card reading and target-card key recovery and remembered between sessions; concurrent settings changes no longer overwrite each other
 - ChameLab: clearer key recovery progress and sector states; assigning a card to a slot closes the assignment panel
 - Website: refreshed responsive layout, improved readability and a higher-resolution device image
 - ChameLab: always show the app version under the title; available updates appear as a small icon beside ChameLab, with the title opening the website's Download section. Header details collapse one at a time only when their contents no longer fit

## [v1.0.19][2026-10-28] · firmware v2.3.3-cl
 - ChameLab: MIFARE Classic keys can be recovered with the Hardnested attack (desktop only)
 - ChameLab: hide the empty memory panel when choosing to scan, create or import a card
 - ChameLab: Android shows a splash screen with the ChameLab logo (it was cropped)
 - ChameLab: a card saved without its memory having been read (a new card, a scan, or a dump stopped before any key was found) keeps its sectors unread instead of saving blank memory that showed every sector as read
 - ChameLab: Android connects to the Chameleon over BLE more reliably (LE transport, faster connection interval, acknowledged writes), instead of timing out on the first command
 - ChameLab: mfkey32 now runs from the card editor's attack toolbar using a slot that already holds the card. The progress panel guides reader capture, saves recovered keys against the card's full UID, and prompts for the original card before reading it. Stop becomes a green Done button after completion; cancellation and errors restore the device mode, active slot and emulator settings. The Tools page has been removed

## [v1.0.18][2026-10-27] · firmware v2.3.3-cl
 - ChameLab: a MIFARE Classic dump can recover unknown sector keys with the Darkside, Nested and Static nested attacks, run in the app — the buttons appear next to Dump once a scan has found which ones the card is open to (Darkside when the card is vulnerable and some keys are still missing; Nested and Static nested once at least one key is known, matched to the card's PRNG), and each one reads back the blocks it just unlocked
 - ChameLab: scanning a MIFARE Classic card now detects its PRNG type and whether it is open to Darkside, so the recovery buttons offered fit the card
 - ChameLab: keys recovered for a card are remembered in a read-only "Known card keys" dictionary, indexed by UID, and tried first the next time that card is dumped; it is shown in Dictionaries with its card and key counts

 - ChameLab: dumping a MIFARE Classic card now shows a key grid instead of a plain block strip — one cell per sector (16 per row, so 1K is one row and 4K three), each cell two stacked squares (key A over key B) colored by the key-search state; the key being searched fills up as candidates are tried, and the progress line names the sector and key with a bar for the search. Hover a sector for its blocks and recovered keys
 - ChameLab: the MIFARE Classic dump summary is down to sectors read and time; the rest is on the key grid
 - ChameLab: changing a scanned card's type drops its dump (grid, summary and the memory it read) and sets the dump up for the new type — a MIFARE Classic dump reads as many sectors as the chosen type has, so a card picked as 2K or 4K gets all of them; the type picker of a scanned card only offers types of its frequency (no MIFARE for an LF tag)
 - ChameLab: MIFARE Classic 2K cards keep all 128 blocks when dumped, exported as .bin / .eml or written to a card (they were cut to 1K)
 - ChameLab: a MIFARE Classic dump recovers key A and key B for every sector independently (before it stopped at whichever key opened the sector first), and writes the recovered keys into the sector trailers so the saved dump carries them
 - ChameLab: a MIFARE Classic dump finds keys faster: keys that opened earlier sectors are tried first for both key A and key B, a known key the card rejects is not retried, and the dictionary pass skips keys already tried on the sector
 - ChameLab: stopping a MIFARE Classic dump now keeps what was read so far, so a partial dump can be saved (it was discarded before)
 - ChameLab: the memory editor sets MIFARE Classic sectors apart, each under a "Sector N · blocks a–b" heading, and the memory summary counts complete sectors (15/16 sectors) instead of blocks
 - ChameLab: the key dictionaries picked for a dump are remembered between sessions
 - ChameLab: the Tag type box no longer comes up empty now and then when switching between cards
 - ChameLab: the highlighted page in the navigation and the phone tab bar no longer goes astray when connecting or disconnecting a device

## [v1.0.16][2026-10-26] · firmware v2.3.3-cl
 - ChameLab: the navigation is always shown, starting on Connect; once a device is connected, Connect gives way to Chameleon (formerly Dashboard), which now also holds the device settings and resets. Settings keeps the app-side ones. Without a device the app works with saved cards, dictionaries and settings, so Continue without device is gone
 - ChameLab: narrow screens (phones) get a bottom tab bar with the pages a tap away
 - ChameLab: optional scan sounds and vibration (Settings): a soft chime when a card scan or dump finishes, a buzz when no card is found; vibration on Android, iOS and mobile browsers
 - ChameLab: Add card and Add folder buttons on the Cards item of the navigation pane; Add card opens Cards from any page and starts a new card
 - ChameLab: all text buttons and drop-downs share one height, so swapping Dump for Stop dump no longer shifts the layout
 - ChameLab: the MIFARE Classic and Ultralight emulator settings of a slot are indented under their headings

## [v1.0.15][2026-10-26] · firmware v2.3.3-cl
 - ChameLab: a dragged card shows its tile at its real size on scaled screens (phones); v1.0.14 fixed only the frame around it

## [v1.0.14][2026-10-26] · firmware v2.3.3-cl
 - ChameLab (Android): the navigation pane shows the app version instead of 0.0.0-local
 - ChameLab: a dragged card looks like the card on scaled screens (phones), not a blown-up corner of it

## [v1.0.13][2026-10-26] · firmware v2.3.3-cl
 - Release notes list the SHA-256 checksums of the files
 - v1.0.12 was not published (its release could not be created); its changes ship in this release

## [v1.0.12][2026-10-26] · firmware v2.3.3-cl
 - ChameLab: saved cards can be filed in folders (one level), shown A–Z (Folder 2 before Folder 10) indented under Cards in the navigation pane with their card counts; Cards itself lists the cards in no folder, followed by the folders as tiles, and the back arrow beside a folder's title returns to it. The + on Cards adds a folder, and the pencil / bin buttons on a folder (on hover, or right-click) rename or delete it (its cards go back to Cards). Drag a card onto a folder (in the navigation pane or a tile) to move it there, or onto Cards to take it out; the Folder button of an open card does the same. New and imported cards go into the folder shown
 - ChameLab: the navigation pane keeps its layout while it slides open or shut
 - ChameLab: card, dictionary and slot rows light up on hover like card tiles, and the gaps between rows (and between navigation items) belong to the rows, so a click or hover never falls through
 - ChameLab: a scanned card that is already saved is shown under Already saved, with a tile that opens the saved card

## [v1.0.11][2026-10-25] · firmware v2.3.3-cl
 - ChameLab: writing an LF card to a T55xx tag has Current password and New password fields: the current one unlocks a locked tag, the new one is what the tag is locked with afterwards (empty keeps the current password, or 20206666 for a blank tag)
 - ChameLab: a T55xx write is checked by reading the tag back, so a write that did not take (most often a tag locked with another password) shows an error instead of reporting success
 - ChameLab: tags ChameLab locked earlier with its default password (20206666) can get a new password without entering the current one
 - ChameLab: the Operating mode switch on the Dashboard is sized to its labels

## [v1.0.10][2026-10-25] · firmware v2.3.3-cl
 - ChameLab: mfkey32 key recovery wizard in Tools: puts a blank MIFARE Classic card on a free slot in tag mode, captures a reader's authentication attempts as you present the device, recovers the sector key in the app and saves it to the “Recovered keys” dictionary; the device mode, active slot and the slot it used are restored when you finish or leave the page
 - ChameLab: writing a card shows a block strip like a dump (written, writing, write error, not written) with the progress next to the buttons; Cancel stops the write (blocks already written stay on the card) and then turns into Close
 - ChameLab: the magic card generation chosen or detected for a write is remembered instead of falling back to Gen2
 - ChameLab: unplugging the device during a write or dump no longer freezes the app; the operation ends with “The Chameleon was disconnected.”
 - ChameLab: ATQA is shown high byte first (00 04), as in ChameleonUltraGUI; block 0 of a new card gets it in the right order, and cards or older Flipper files saved with the bytes swapped are fixed on load

## [v1.0.9][2026-10-24] · firmware v2.3.3-cl
 - ChameLab: the web app connects over Bluetooth LE in Chrome / Edge (Web Bluetooth), next to USB; firmware updates still need USB or the desktop / mobile app
 - ChameLab: the Bluetooth scan on Android and iOS lists only devices that advertise the Nordic UART service
 - ChameLab: Settings has a single Save that sends and stores the values, with a bar for unsaved changes and Discard; unsaved edits survive leaving the page
 - Merged upstream ChameleonUltra v2.3.0:
   - Added Farpointe/Pyramid LF protocol support: read and emulate (FSK2a, RF/50); `lf pyramid read`
   - Battery condition hint (`excellent` … `critical`) in `hw battery` and on the device's battery screen, new `DATA_CMD_GET_BATTERY_INFO_EX`
   - More robust FSK demodulation on noisy/DC-offset signals (HID Prox, ioProx)
   - Fix CLI non-interactive help and add one-shot `--command` flag; Python client reformatted with ruff
 - Slot color commands moved to ChameLab's own range: `DATA_CMD_SET_SLOT_COLOR` 1043 → 1900, `DATA_CMD_GET_ALL_SLOT_COLORS` 1044 → 1901 (1043 is upstream's `GET_BATTERY_INFO_EX`); ChameLab needs the new firmware for slot colors
 - Farpointe/Pyramid emulation from apps: new `DATA_CMD_PYRAMID_SET_EMU_ID` (5900) / `DATA_CMD_PYRAMID_GET_EMU_ID` (5901) and `lf pyramid econfig` (`--raw` frame or `--fc`/`--cn` for 26-bit cards)
 - ChameLab: scans Farpointe/Pyramid cards and emulates them in a slot; the card editor builds a 26-bit Pyramid ID from a facility code and card number and shows what an ID decodes to
 - ChameLab: the Dashboard shows the battery condition (Excellent … Critical) on firmware that reports it

## [v1.0.8][2026-10-23] · firmware v2.3.2-cl
 - ChameLab: checks GitHub for a newer version at startup and shows an Update button in the top bar and beside the version in the navigation pane; Settings › About has Check for updates (not in the web app, which is always current)
 - ChameLab: the Connect page finds Bluetooth devices on its own while it is open, with hints that follow the platform's transports

## [v1.0.7][2026-10-23] · firmware v2.3.2-cl
 - Separate versions: releases carry the ChameLab version, the firmware reports its own (v2.3.2-cl) and only changes it when the firmware changes
 - Release files carry their version in the name (`chamelab-1.0.7-win-x64.zip`, `ultra-dfu-app-2.3.2-cl.zip`); the development build is versioned by the commits since the last release (`1.0.7-dev.N`); the download page shows the release date and time

## [v2.3.2][2026-10-23]
 - ChameLab: firmware updates over Bluetooth find and connect to the bootloader reliably; a timeout names the last error
 - ChameLab: the firmware update dialog keeps its width and shows the release notes as Markdown

## [v2.3.1][2026-10-23]
 - ChameLab: the slot's card is matched to Saved cards by UID; the active slot no longer opens by itself on narrow screens
 - ChameLab: compact Dashboard slot strip with the numbers inside the LEDs, and a cleaner active slot line

## [v2.3.0][2026-10-23]
 - Fix wrong data reported by hf 14a sniff on frames containing more than 7 parity bits (@DidierA)
 - Added MIFARE Ultralight C authentication and emulation (@Foxushka)
 - Added per-card slot LED colors: the RGB lines are driven by PWM to show any color, a slot holding an HF and an LF card alternates between their colors; new `DATA_CMD_SET_SLOT_COLOR` / `DATA_CMD_GET_ALL_SLOT_COLORS` and `hw slot color`

## [v2.2.0][2026-07-04]
 - Added Jablotron LF protocol support: read, emulate and T55xx clone (@midlan)
 - Added IDTECK LF protocol support: tag emulation (PSK1 RF/32) and T55xx clone. No reader path yet; PSK demodulation on the envelope-only receive chain is left for a follow-up.
 - Added PAC/Stanley LF protocol support: read, emulate and T55xx clone (@kevihiiin, @danieltwagner)
 - Fix firmware application USB serial number (@taichunmin)
 - Added ioProx LF protocol support (read, emulate and T55xx clone)
 - Added `hf mfu nfcimport` to import Flipper Zero `.nfc` files into MFU/NTAG emulator slots, with `--amiibo` flag for automatic PWD/PACK derivation (@fmuk)
 - Added commands to dump and clone Mifare tags
 - Fix bad missing tools warning (@suut)
 - Fix for FAST_READ command for nfc - mf0 tags
 - Rewrite of the dynamic and static locks logic for NTAG213, NTAG215 and NTAG216; we shouldn't take into account the block lock bits
 - Fixed an issue where we wouldn't be able to change CFG0 and CFG1 for NTAG213, NTAG215 and NTG216 once a password was added even if the cfg bit was reset.
 - Fix for static nested key recovery (@jekkos)
 - Fix LEDs being stuck on after battery check (@suut)
 - Add TCP support for the CLI (@suut)
 - Fix build on Android in Termux (@suut)
 - Fix the issue where some reader cause CU to enter a strange state (@xianglin1998)
 - The transmission performance of USB has been improved (@xianglin1998)
 - Added cmd for set mf1 config 'field_off_do_reset' (@xianglin1998)
 - Fix Windows build (@suut)
 - Added `hf 14a config` to deal with badly configured cards (@azuwis)
 - New Symmetrical LED Animation Mode and Improved Minimal Mode (@WillyJL)
 - Fixed formatting returned keys by static and staticnested (@triplesprawl)
 - Fix MF1 state reset logic and access control conditions (@unkernet)
 - Added support for SEOS credentials (@aaronjamt)

## [v2.1.0][2025-09-02]
 - Added UV, formatter and linter. Contribution guidelines. (@GameTec-live)
 - Extend max packet data size from 512 to 4096 bytes (@Foxushka)
 - HID Prox support (@TeCHiScy)
 - `hf mf elog --decrypt` skip records with found keys (@taichunmin)
 - Added cmd for fetching all slots nicks (@Foxushka)
 - Added `hf mf senested` for recovering keys from static encrypted cards via backdoor (https://eprint.iacr.org/2024/1275) (@Foxushka)
 - Added cmd for faster bulk key checking on one block (~33 keys per second) (@Foxushka, @taichunmin)
 - Added cmd to acquire nonces for static encrypted cards via backdoor (@Foxushka) 
 - Added `firmware/docker-compose.yml` to build firmware in local docker (@taichunmin)
 - Added cmd to acquire nonces for hardnested(Protocol doc need update) (@xianglin1998)
 - Added command to check keys of multiple sectors at once (@taichunmin)
 - Fixed unused target key type parameter for nested (@petepriority)
 - Skip already used items `hf mf elog --decrypt` (@p-l-)
 - Parallelize mfkey32v2 processes called from CLI (@p-l-)
 - Added support for mifare classic value block operations (@taichunmin)
 - Added regression tests (@doegox)
 - Changed git version to version tag instead of dev tag (@taichunmin)
 - Fixed 14A emulate bug and MF1 emulate bug (@spp2000 and @xianglin1998)
 - Fixed `hf 14a raw` command raising `AttributeError` (@augustozanellato)
 - Fixed ATS handling in tags that NAK RATS (@augustozanellato)
 - Changed battery level curves based on experimental measures (@spp2000)
 - Added multithreading on Nested and StaticNested (@xianglin1998)
 - Fixed factory reset hanging (@augustozanellato)
 - Changed fds_write_sync to take length in bytes instead of next multiple of 4 (@doegox)
 - Fixed field LED when LF reading and HF cloning (@doegox)
 - Added renaming of slot into "cloned" when having cloned an ID/UID with a button (@doegox)
 - Fixed spurious LED in battery level, assigned battery level to long press B (@doegox)
 - Changed `hw slot list` to display LF ID & HF anticol data, and names in the --short version too (@doegox)
 - Added colors to CLI help (@doegox)
 - Changed massively CLI, cf https://github.com/RfidResearchGroup/ChameleonUltra/issues/164#issue-1930580576 (@doegox)
 - Changed CLI help: lists display and now all commands support `-h` (@doegox)
 - Added button action to show battery level (@doegox)
 - Added GUI Page docs (@GameTec-live)
 - Changed CLI threads polling into blocking reads, to reduce CPU usage (@doegox)
 - Added support for timestamped comments in CLI via `rem`, `;`, `%` or `#` (@doegox)
 - Fixed watchdog trigger during `hw factory_reset` (@doegox)
 - Added PyInstaller support for CLI client (@augustozanellato)
 - Added proper Mifare Ultralight (original, C, EV1) / NTAG (213, 215, 216) emulation (@turbocooler).


## [v2.0.0][2023-09-26]
 - Added `hw slot nick delete` and DELETE_SLOT_TAG_NICK (@doegox)
 - Changed APP_FW_VER now deduced from git tag vx.y.z (@doegox)
 - Changed initial button wakeup from 4 to 8 seconds (@aramova)
 - Added MIFARE Ultralight reading features (@FlUxIuS & @doegox)
 - Fixed MF1 write mode SHADOW was not preserved properly (@doegox)
 - Changed field LED now active also in reader mode to indicate that reader is powering the field (@doegox)
 - Changed slot enabled logic: now we have separate enabled_hf and enabled_lf, changed GET_ENABLED_SLOTS and SET_SLOT_ENABLE (@doegox)
 - Changed tag type enum to be ready for new types, changed stored slotConfig and GET_SLOT_INFO (@doegox)
 - Added HF14A_RAW and its support in `hf 14a raw` (@xianglin1998)
 - Removed MF1_DETECT_DARKSIDE (@doegox)
 - Added MF1_STATIC_NESTED_ACQUIRE and its support in `hf mf nested` (@xianglin1998)
 - Changed `hf 14a scan`: Automatically send RATS to 14443-4a tags (@augustozanellato)
 - Changed Darkside: use LEDs for visual feedback of attack progression (@doegox)
 - Changed Darkside: longer RF field off for reset and longer CLI timeout (@doegox)
 - Fixed Darkside: parity byte-to-array bug made it low probability to succeed (@doegox)
 - Changed `hw detection decrypt` show progression and remove duplicate keys (@doegox)
 - Changed dynamic cmd_map_init() by static cmd_map initialization (@doegox)
 - Changed `hf slot list` to add clarity and colors (@doegox)
 - Changed `hf mf sim` and `hf mf info` to support ATS (still to be used in actual emulation) (@doegox)
 - Changed `hf mf eload` and `hf mf eread`: uploads/downloads are now 30x faster (@doegox)
 - Changed CLI HF14AInfo logic merged inside HF14AScan for more consistent display of the results (@doegox)
 - Added guessed type information for NXP tags, and reorganization of HF information part. (@FlUxIuS)
 - Changed `hw raw` to detail status message (@doegox)
 - Changed CLI to query capabilities on connect, not on every single command if device does not support get_device_capabilities (@doegox)
 - Changed CLI to not instanciate ChameleonCMD on every single command (@doegox)
 - Changed massively the protocol and its handlers for more consistency and easier maintenance and future dev (@doegox)
 - Added `hf settings blepair` command to get and set ble pairing enable state, and default disable ble pair (@xianglin1998)
 - Added `hf mf info` command to get UID/SAK/ATQA from slot (@Foxushka)
 - Added `hw raw` to send raw command to Chameleon (@Foxushka)
 - Added command to fetch all available commands from Chameleon and test if Chameleon supports it (@Foxushka)
 - Fixed ChameleonLite emulation bug (@spp2000)
 - Fixed MFC emulation issues with OEM readers, also temporarily disabling NFC_MF1_FAST_SIM (@xianglin1998)
 - Fixed Chameleon crash during BLE pairing (@Foxushka)
 - Fixed CLI takes into account Lite refusing to enter Reader mode (@doegox)
 - Added BLE Security support (@xianglin1998)
 - Added `hw settings blekey` to get and set ble connect key (@xianglin1998)
 - Added `hw ble bonds clear` to delete all ble bonds (@xianglin1998)
 - Fixed argument completion bug inserting additional hyphens (@szymex73)
 - Fixed device address not the same within app (@taichunmin)
 - Added initial version of the user guides (@GameTec-live)
 - Added support for pasting several command lines at once with prompt_toolkit (@doegox)
 - Added support for interrupting sleep sequence with a button press during animation (@doegox)
 - Fixed logs corruption and app reset on FDS write, added logs flush on sleep (@doegox)
 - Added support for long-press of buttons (@nemanjan00)
 - Changed `hw slot delete`, now it can always delete from slot (@augustozanellato)
 - Changed CI pipeline (@augustozanellato)
 - Added offline copy EM card uid for btnpress (@nemanjan00)
 - Added offline copy ic card uid for btnpress (@xianglin1998)
 - Added `hw settings btnpress` to get and set button press function (@xianglin1998)
 - Added `hw battery` to get battery information (@xianglin1998)
 - Added `hw slot delete` to delete HF or LF out of a HF+LF slot (@augustozanellato)
 - Changed CLI prompt autocompletion, saved history and internal cmd registration (@szymex73)
 - Fixed SDK NFC IRQ handler busy loop (@doegox)
 - Added `hf mf eread` to download slot dump to file (@domints)
 - Added `lf em sim get` and old `lf em sim` is now `lf em sim set` (@domints)
 - Changed MF1 command ids (@domints)
 - Changed nonce randomness on nested auth for hardnested attack (@Foxushka)
 - Added commands to configure MF1 emulation magic modes and more (@Foxushka)
 - Added `hw factory_reset` to wipe user data (@augustozanellato)
 - Changed slot nickname encoding gbk -> utf8 (@doegox)
 - Added 5-second watchdog (@doegox)
 - Added option to enable NRF_LOG UART on SWO pin (@doegox)
 - Added `hw slot list` (@Foxushka)
 - Added `hw version` (@nemanjan00 and @augustozanellato)
 - Fixed data sanity check in MF1 emulation (@sgnusov)
 - Fixed settings/dumps/nicks data corruption in flash (@doegox)
 - Fixed slot LED on button wake-up (@doegox)
 - Added support for init, reset and migrate settings (@augustozanellato)
 - Added support to save settings in flash (@domints)
 - Added `hw settings animation` to configure startup animation (@domints)
 - Fixed compilation warnings from SDK (@doegox)
 - Changed TagSenseType, TagSpecificType and SlotNumber enums (@F9Alejandro and @doegox)
 - Added support for 7-byte UID MF1 emulation (@xianglin1998)
 - Added high perf MF1 emulation (@xianglin1998)
 - Added udev rules file (@iceman1001)
 - Added documentation to use BlackMagicProbe out of ST-Link and show RTT NRF_LOG (@doegox)
 - Added `hw chipid` and `hw address` (@gentilkiwi)
 - Added helper scripts to enter DFU from USB or BLE and flash over DFU (@doegox)
 - Fixed recv thread exception after `hw dfu` (@doegox)
 - Fixed entering sleep after BLE disconnection (@doegox)
 - Added ChameleonLite and ChameleonUltra hardware resources (@xianglin1998)
 - Added NTAG21x emulation support (@ca1e)
 - Fixed LF EM410x emulation (@doegox)
 - Added automatic file format detection in `hf mf eload` (@GameTec-live)
 - Fixed CLI exit exception (@doegox)
 - Added CI pipeline, producing DFU packages (@augustozanellato)
 - Added docker toolchain (@augustozanellato)
 - Added automatic port detection (@GameTec-live)
 - Added GET_MF1_DETECTION_STATUS (@Foxushka)
 - Fixed compilation errors with GCC 12 (@Foxushka)
 - Added documentation for JLink (@xianglin1998)
 - Added support for ST-Link and debugging documentation (@derGraph)

## [v1.0][2023-06-06]
