# MIFARE Classic Auto recovery

Auto replans after every confirmed key. It never infers that the next key will
take as long as the preceding one. The current order is:

1. Confirm saved full-UID keys, preferring their last known sector and key type.
2. Read accessible B keys with A and propagate each confirmed key to missing pairs.
3. Try four supported SEN backdoors, require two matching captures, and test the
   selected dictionaries against that evidence locally before any heavy recovery.
4. Schedule a bounded set of SEN paths by candidate count and reuse benefit.
   Pair-seed compatibility prioritizes candidates; incompatible remainders are
   retained for cards that do not follow the RF08S seed relationship.
5. Use the cached PRNG profile to select Nested, Static nested or Hardnested.
   Without a seed, use applicable Darkside, then search the selected dictionaries
   until a seed appears. A new seed can also enable SEN through normal authentication.
6. After two inconclusive Nested attempts, discard its calibration and move to
   Hardnested when available. Explicit nonce-target refusals are tracked separately.
   Finish with the selected dictionaries for pairs still unresolved.

The SEN working set is at most two paths in the browser, four on mobile, or eight
on desktop, with a soft allocation budget of 16/16/64 MiB respectively. A single
recovery result must fit before its size is known. Only this bounded set is
cached; the planner avoids retaining candidate lists for the entire card.
The measured authentication and batch costs belong to the current device
connection, so USB timings are not reused for another BLE connection.
These are heuristics, not a proof of the globally shortest route.

The session is attached to the scanned dump and client connection. Scan probing,
dictionary checks, propagation and automatic attacks share confirmed rejections.
Each rejected key uses one sector bit mask, rather than up to 80 separate hash
entries for that key, keeping large dictionary sessions compact.
No failed radio exchange rejects a candidate. Interrupted firmware commands are
drained; successful keys from their replies are retained before honoring Stop.
SEN captures and pending candidate lists survive Stop within the same session.
Rescanning or reconnecting clears transient evidence. The persistent UID database
keeps confirmed keys and their positions through an optional `sectors` field in
the existing version-2 JSON format. Older backups without that field still work.

## Normal-key SEN collection (2023)

Firmware 2.3.16-cl advertises `DATA_CMD_MF1_ENC_NESTED_WITH_KEY` (2023) on reader
devices. The request is 10 bytes: `key[6], sector_count, starting_sector,
known_block, known_type`. The type is 0x60 or 0x61. Sector count is 1..40 and the
starting sector must be smaller. The host currently requests all sectors from zero.

On success the response is the authentication UID (4 bytes, big endian), then
14 bytes per sector: A and B each have `nonce_upper16[2], parity_errors[1],
encrypted_nonce[4]`. This is the same record format as command 2014. The device
calibrates the distance from a decrypted known-key nested nonce to the following
first-auth nonce and rewinds following nonces by that distance. The host checks
the physical full UID and requires a second matching capture for every missing
key before using the evidence. The inferred calibration nonce must also explain
the known seed key. Timing instability falls back to other methods.
Existing firmware continues to support the backdoor path through 2014/2015.

The fallback follows the normal-key branch of Proxmark3's
[`MifareAcquireStaticEncryptedNonces`](https://github.com/RfidResearchGroup/proxmark3/blob/c7b80e1bc89ed03983832f93b1d2b5f980c8bc9b/armsrc/mifarecmd.c#L1290)
(GPLv3). Host protocol, ordering, cancellation and synthetic cryptographic
evidence are covered by tests. RF timing and behavior on physical RF08S variants
still require hardware validation.

Run the firmware's synthetic RF peer with `python3 firmware/tests/test_mifare_recovery.py`
(GCC required). It executes the production collection and checking loops with
the firmware's Crypto1/PRNG code, covering nonce rewind, all 40 sectors, partial
sector ranges, readable zero B and error propagation. The browser worker harness
also checks the offline dictionary operation against reference SEN candidates.
