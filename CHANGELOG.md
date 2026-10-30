# Change Log
All notable changes to this project will be documented in this file.
This project uses the changelog in accordance with [keepchangelog](http://keepachangelog.com/). Please use this to write notable changes, which is not the same as git commit log...

Releases are tagged with the ChameLab version, which goes up with every release (v1.0.7 on).
The firmware has its own version in `firmware/VERSION` (v2.3.2-cl on), bumped only when the firmware changes.

## [unreleased][unreleased]
 - ChameLab: switching pages is instant: the pages are built in the background while the app is idle, and then only shown or hidden (it used to be rebuilt and cross-faded every time, about half a second on phones). Pages keep their scroll position between visits

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
