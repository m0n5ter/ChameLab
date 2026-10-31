# LF card cycling

An EM410x slot can transmit its primary UID followed by up to seven additional
UIDs, in order, repeating while a reader's field remains present. A new field
starts at the primary UID. This is supported by both Ultra and Lite firmware.
Other LF protocols, including Electra, retain single-card emulation.

The slot editor shows an ordered list of linked card tiles. Use **Choose saved**
for the first card; the same button becomes **Add saved** to append compatible
EM410x cards, up to eight total. Cards already in the list are excluded from
the picker. Drag tiles to move them and use the delete button inside each tile to remove
it. The first tile is the primary card. Removing it promotes the next card;
removing the last card clears the LF side. Save the slot to persist the list,
or discard edits to restore the saved order. Previously stored UIDs absent
from the library remain in the list and can be added to Saved cards.

The time setting is 1–10 bursts per card. Each burst contains ten complete
EM410x/64 frames (327.68 ms) and the existing field check (about 2 ms).
The default is one burst. With eight cards, a complete pass takes about 2.64 s
at that setting. Readers may require a longer dwell or removal after an unknown
UID. Test on the actual readers; emulation receives no access-granted feedback.

**Silence between cards** adds 0–3 seconds without LF modulation between each
pair of cards, including the last and first cards, in steps of 0.1 seconds.
The default is zero; it adds no delay beyond the existing field check. No gap
precedes the first card. During silence a timer checks the field every 100 ms;
field loss cancels the pause and the next detected presentation starts at the
first card. Switching slots or leaving tag mode also cancels the pending pause.
The setting is saved with the slot and follows it during slot swaps. It is
shown only on firmware that advertises the new silence commands.

Firmware without both cycle commands uses the original single-card LF editor:
the linked card tile, **Choose saved** to replace the current card, **Clear**,
and the LED settings. The multi-card list, count, drag reordering and cycle
settings remain hidden. Support is determined from device capabilities, not
from the firmware version string. Silence support is checked separately.

Slot swaps preserve the list. Uploading a single card through the normal upload
command clears its previous list. Saving a card from a slot to the card library
exports the primary card only. The cycle editor appears only when the device
advertises support for the new commands.

## Commands

Both commands operate on the active slot and require the EM410x tag type.

| Command | ID | Request | Response |
| --- | --- | --- | --- |
| Set EM410x cycle | 5902 | bursts: u8, extra count: u8, extra UIDs: count × 5 bytes | Success, empty payload |
| Get EM410x cycle | 5903 | Empty | bursts: u8, extra count: u8, extra UIDs: count × 5 bytes |
| Set EM410x cycle silence | 5904 | silence: u8 (0–30, in 100 ms units) | Success, empty payload |
| Get EM410x cycle silence | 5905 | Empty | silence: u8 (0–30, in 100 ms units) |
| Get all EM410x cycles | 5906 | Empty | 8 × 44 bytes, one entry per slot (see below) |

Get all EM410x cycles reads every slot without switching the active slot, so
clients avoid the LED slot animation. Each 44-byte entry is: is EM410x (u8, 1 or
0; a zero entry is all zeros), bursts, extra count, silence, primary UID (5 bytes),
then seven five-byte extra UID positions (unused positions are zero). The active
slot is read from RAM, including unsaved edits; the others from flash.

Bursts must be 1–10 and extra count 0–7. Invalid types, lengths and ranges
return `STATUS_PAR_ERR` without modifying the slot. Count zero clears cycling;
GET then returns `[1, 0]`. SET updates RAM; command 1009 saves to flash.
Legacy GET-ID returns the primary UID and SET-ID replaces it and clears cycling.
SET-CYCLE clears the previous silence. To save a nonzero pause, SET-CYCLE,
SET-CYCLE-SILENCE, then save to flash. GET-CYCLE retains its original wire
format; clients read the silence separately when supported. Older firmware
continues to support cycling with zero added silence.

The existing LF flash record begins with the primary five-byte UID. Cycling
extends it to 44 bytes: marker `C7`, version `01`, total card count, bursts,
then seven five-byte UID positions (unused positions are zero). V2 stores 48
bytes, uses version `02`, and appends the silence byte plus three padding bytes.
V2 permits a one-card record so the setting can be retained with a single card;
pauses are only applied when there are multiple cards. Old single-card and v1
records remain readable. Waveforms are prepared before emulation; interrupts
only select a prepared sequence after PWM stops, never interrupting a frame.

## Hardware verification

Before release, check single-card emulation, two/eight-card order, different
dwell settings, field removal/reapplication, restart persistence, slot swaps,
and replacing/clearing a cycling slot on real Ultra and Lite devices. Check
silence at 0, 0.1 and 3 seconds, removing the field during a pause, and cancelling
a pause by switching slots or leaving tag mode. Verify
the accepted UID can occur at every list position on the intended readers.
