# LF card cycling

An EM410x slot can transmit its primary UID followed by up to seven additional
UIDs, in order, repeating while a reader's field remains present. A new field
starts at the primary UID. This is supported by both Ultra and Lite firmware.
Other LF protocols, including Electra, retain single-card emulation.

In the slot editor, choose the primary EM410x card, then add additional cards
from Saved cards or enter their ten-digit hexadecimal UIDs, one per line.
Line order is transmission order; remove a line to remove a card. Save the
slot to persist the list. An empty list disables cycling.

The time setting is 1–10 bursts per card. Each burst contains ten complete
EM410x/64 frames (327.68 ms) and the existing field check (about 2 ms).
The default is one burst. With eight cards, a complete pass takes about 2.64 s
at that setting. Readers may require a longer dwell or removal after an unknown
UID. Test on the actual readers; emulation receives no access-granted feedback.

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

Bursts must be 1–10 and extra count 0–7. Invalid types, lengths and ranges
return `STATUS_PAR_ERR` without modifying the slot. Count zero clears cycling;
GET then returns `[1, 0]`. SET updates RAM; command 1009 saves to flash.
Legacy GET-ID returns the primary UID and SET-ID replaces it and clears cycling.

The existing LF flash record begins with the primary five-byte UID. Cycling
extends it to 44 bytes: marker `C7`, version `01`, total card count, bursts,
then seven five-byte UID positions (unused positions are zero). Old single-card
records remain readable. Waveforms are prepared before emulation; interrupts
only select a prepared sequence after PWM stops, never interrupting a frame.

## Hardware verification

Before release, check single-card emulation, two/eight-card order, different
dwell settings, field removal/reapplication, restart persistence, slot swaps,
and replacing/clearing a cycling slot on real Ultra and Lite devices. Verify
the accepted UID can occur at every list position on the intended readers.
