# LF card cycling

An LF slot can transmit its own card followed by up to seven additional cards, in order,
repeating while a reader's field remains present. Cards of every emulated LF type can be
mixed freely in one list: EM410x, EM410x Electra, HID Prox, ioProx, Viking, PAC,
Jablotron, IDTECK and Farpointe Pyramid. A new field starts at the first card. This is
supported by both Ultra and Lite firmware.

The slot editor shows an ordered list of linked card tiles. Use **Choose saved**
for the first card; the same button becomes **Add saved** to append more LF cards, up to
eight total. Cards already in the list are excluded from the picker. Drag tiles to move
them and use the delete button inside each tile to remove it. The first tile is the
slot's own card and sets the slot type. Removing it promotes the next card; removing the
last card clears the LF side. Save the slot to persist the list, or discard edits to
restore the saved order. Previously stored cards absent from the library remain in the
list and can be added to Saved cards.

The time setting is 1–10 bursts per card. Each burst contains ten complete frames of
the card plus the existing field check (about 2 ms), so its length depends on the
protocol:

| Type | One burst |
| --- | --- |
| Viking, IDTECK | ≈ 0.16 s |
| EM410x, ioProx, PAC | ≈ 0.33 s |
| HID Prox | ≈ 0.38 s |
| Farpointe Pyramid | ≈ 0.51 s |
| EM410x Electra, Jablotron | ≈ 0.65 s |

The default is one burst. With eight EM410x cards, a complete pass takes about 2.64 s
at that setting. Readers may require a longer dwell or removal after an unknown UID.
Test on the actual readers; emulation receives no access-granted feedback.

**Silence between cards** adds 0–3 seconds without LF modulation between each
pair of cards, including the last and first cards, in steps of 0.1 seconds.
The default is zero; it adds no delay beyond the existing field check. No gap
precedes the first card. During silence the antenna is detuned and the field LED is
off, so the card looks removed; the field is checked when the gap ends, and field loss
means the next detected presentation starts at the first card. Switching slots or
leaving tag mode also cancels the pending pause. The setting is saved with the slot and
follows it during slot swaps.

Firmware without the cycle commands uses the original single-card LF editor:
the linked card tile, **Choose saved** to replace the current card, **Clear**,
and the LED settings. The multi-card list, count, drag reordering and cycle
settings remain hidden. Support is determined from device capabilities, not
from the firmware version string.

Slot swaps preserve the list. Uploading a single card through the normal upload
command of any LF type clears its previous list. Saving a card from a slot to the card
library exports the slot's own card only.

## Commands

The commands operate on the active slot (except Get all LF cycles), which must hold an
emulated LF type. Each extra card is 18 bytes: its tag type (u16, big-endian, as in the
slot type commands) and 16 data bytes, the same emulator data its own set-emu-ID command
takes, zero-padded.

| Command | ID | Request | Response |
| --- | --- | --- | --- |
| Set LF cycle | 5907 | bursts: u8, silence: u8, extra count: u8, extra cards: count × 18 bytes | Success, empty payload |
| Get LF cycle | 5908 | Empty | Same as the Set LF cycle request |
| Get all LF cycles | 5909 | Empty | 8 × 147 bytes, one entry per slot (see below) |

Bursts must be 1–10, silence 0–30 (in 100 ms units), extra count 0–7 and each type an
emulated LF type. Invalid types, lengths and ranges return `STATUS_PAR_ERR` without
modifying the slot. Count zero with zero silence clears cycling; GET then returns
`[1, 0, 0]`. SET updates RAM; command 1009 saves to flash. The set-emu-ID commands
replace the slot's own card and clear cycling; the get-emu-ID commands return the
slot's own card.

Get all LF cycles reads every slot without switching the active slot, so clients avoid
the LED slot animation. Each 147-byte entry is: the slot's LF type (u16, big-endian;
0 when the slot has no emulated LF card, and the rest of the entry is zero), the slot's
own card (16 bytes, zero-padded), then the Get LF cycle payload zero-padded to seven
extra cards. The active slot is read from RAM, including unsaved edits; the others from
flash.

The LF flash record begins with the slot's own card. Cycling extends it to 152 bytes:
the own card padded to 16 bytes, marker `C7`, version `04`, total card count, bursts,
silence, three padding bytes, then seven 18-byte extra card positions (unused positions
are zero). A one-card record is permitted so the silence setting can be retained with a
single card; pauses are only applied when there are multiple cards. Lists stored by
earlier firmware (versions `01` and `02`) are not read; the slot keeps its own card.

A waveform can take several kilobytes (IDTECK: 1024 PWM entries), too much to keep one
per card, so only the card being sent is modulated. The next card is modulated, and the
PWM base clock switched for IDTECK's PSK1 (1 MHz instead of 125 kHz), in the PWM
interrupt after PWM has stopped, never interrupting a frame.

## Hardware verification

Before release, check single-card emulation, two/eight-card order, lists mixing
protocols (including IDTECK next to 125 kHz-clock types and two cards of one protocol in
a row), different dwell settings, field removal/reapplication, restart persistence, slot
swaps, and replacing/clearing a cycling slot on real Ultra and Lite devices. Check
silence at 0, 0.1 and 3 seconds, removing the field during a pause, and cancelling a
pause by switching slots or leaving tag mode. Verify the accepted card can occur at
every list position on the intended readers.
