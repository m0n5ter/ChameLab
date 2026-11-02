# LF card cycling

An LF slot can hold its own card plus up to seven additional cards, in order. On each
appearance of a reader's field the device emulates the next card in the list, one steady card
per presentation: tap the reader, it sends one card; tap again, it sends the next. After the
last card it wraps back to the first. Cards of every emulated LF type can be mixed freely in
one list: EM410x, EM410x Electra, HID Prox, ioProx, Viking, PAC, Jablotron, IDTECK and
Farpointe Pyramid. This is supported by both Ultra and Lite firmware.

Sending one steady card per field appearance is the most compatible way to drive access
readers: each presentation is a clean "card appeared" transition, which every reader detects,
and the wrong cards are simply the ones you tap past on the way to the one that opens. There
is no per-card timing to tune — no bursts, no silence between cards.

## Position

The device remembers which card comes next between taps, so repeated presentations walk
through the list. The position lives in a retention register (`GPREGRET`), because an LF field
waking the device from deep sleep does so through a reset, which clears RAM. It therefore
survives sleep but not a battery pull or a full reboot, after which the list starts again at
the first card. Switching slots, or returning to a cycle slot after using another slot, also
restarts at the first card: the position is tagged with the slot it belongs to.

## Editing the list

The slot editor shows an ordered list of linked card tiles. Use **Choose saved** for the
first card; the same button becomes **Add saved** to append more LF cards, up to eight total.
Cards already in the list are excluded from the picker. Drag tiles to reorder them and use the
delete button inside each tile to remove it. The first tile is the slot's own card and sets the
slot type. Removing it promotes the next card; removing the last card clears the LF side. Save
the slot to persist the list, or discard edits to restore the saved order. Previously stored
cards absent from the library remain in the list and can be added to Saved cards.

Firmware without the cycle commands uses the original single-card LF editor: the linked card
tile, **Choose saved** to replace the current card, **Clear**, and the LED settings. The
multi-card list and drag reordering remain hidden. Support is determined from device
capabilities, not from the firmware version string.

Slot swaps preserve the list. Uploading a single card through the normal upload command of any
LF type clears its previous list. Saving a card from a slot to the card library exports the
slot's own card only.

## Commands

The commands operate on the active slot (except Get all LF cycles), which must hold an
emulated LF type. Each extra card is 18 bytes: its tag type (u16, big-endian, as in the
slot type commands) and 16 data bytes, the same emulator data its own set-emu-ID command
takes, zero-padded.

| Command | ID | Request | Response |
| --- | --- | --- | --- |
| Set LF cycle | 5907 | extra count: u8, extra cards: count × 18 bytes | Success, empty payload |
| Get LF cycle | 5908 | Empty | Same as the Set LF cycle request |
| Get all LF cycles | 5909 | Empty | 8 × 145 bytes, one entry per slot (see below) |

Extra count must be 0–7 and each type an emulated LF type. Invalid types, lengths and ranges
return `STATUS_PAR_ERR` without modifying the slot. Count zero clears cycling; GET then returns
`[0]`. SET updates RAM; command 1009 saves to flash. The set-emu-ID commands replace the
slot's own card and clear cycling; the get-emu-ID commands return the slot's own card.

Get all LF cycles reads every slot without switching the active slot, so clients avoid the LED
slot animation. Each 145-byte entry is: the slot's LF type (u16, big-endian; 0 when the slot
has no emulated LF card, and the rest of the entry is zero), the slot's own card (16 bytes,
zero-padded), then the Get LF cycle payload zero-padded to seven extra cards. The active slot
is read from RAM, including unsaved edits; the others from flash.

The LF flash record begins with the slot's own card, padded to 16 bytes. Cycling extends it:
marker `C7`, version `05`, total card count, five reserved bytes (kept for word alignment;
the format once carried bursts and silence here), then seven 18-byte extra card positions
(unused positions are zero). Only a list of two or more cards gets a record; a single card is
just its own card. Format `04`, used by firmware v2.3.6-cl and v2.3.7-cl, has the same card
layout and remains readable: all cards and their order are preserved, and the old timing
settings are ignored. The next slot save upgrades the record to `05`, clearing the obsolete
timing bytes. Format `05` lists written by v2.3.8-cl remain unchanged. Lists that v2.3.8-cl
ignored can be recovered too, provided their flash records have not been overwritten.
Earlier EM410x-only formats (`01` and `02`) are not read; the slot keeps its own card.

## Hardware verification

Before release, check single-card emulation, two/eight-card lists cycling one card per field
appearance in order, lists mixing protocols (including IDTECK next to 125 kHz-clock types and
two cards of one protocol in a row), field removal/reapplication advancing to the next card,
and that the accepted card opens its reader when reached. Check that the position is kept
across sleep (tap, wait for the device to sleep, tap again — the next card should follow),
restarts at the first card after a battery pull, and restarts at the first card when switching
to another slot and back. Also check restart persistence of the list itself, slot swaps, and
replacing/clearing a cycling slot on real Ultra and Lite devices.

When testing an update, populate several slots with two/eight-card format `04` lists, including
mixed LF types, then update without resetting flash. Check the active slot and the other slots
in the app before switching or saving them; all cards and their order must remain. Save the
slots, reboot and check again. Repeat with current format `05` lists.
