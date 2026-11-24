# Device sync protocol (v1)

The wire formats of ChameLab's device sync, as implemented in
`software/chamelab/ChameLab.Core/Sync`. The design and its reasons are in
[sync-plan.md](sync-plan.md). Binary values in JSON are base64url without padding ("b64").

## Identities and keys

| Value | What |
| --- | --- |
| Device id | 16 random bytes, b64; also the HLC node of the device's operations |
| Device key | X25519 key pair made on the device; the public half is in the member list |
| Room secret | 32 random bytes, b64, in the `group` entity's `room` field; replaced when a device is removed |
| Room id | `b64(HKDF-SHA256(ikm = room secret, salt = "", info = "chamelab-sync/room/v1", 32))` |

## Channel

Every connection between two devices is a Noise handshake,
`Noise_XX_25519_ChaChaPoly_SHA256` with the prologue `chamelab-sync/1`, over a packet link:

- **relay room**: one relay frame per packet (see [Relay](#relay));
- **TCP** (local network, and the relay's `/share` and `/join` used for one-time codes):
  each packet is preceded by its length, 4 bytes big endian, at most 1 MiB.

The initiator's first handshake message carries, unencrypted, what the connection is for:
`{"p": "sync"}` (two members of a group), `{"p": "join"}` (adding a device) or
`{"p": "cards"}` (sending cards). The other handshake payloads are empty.

After the handshake, messages are UTF-8 JSON objects of any size, split into Noise transport
messages: each plaintext is one flag byte (`1` = last piece of the message, `0` = more follows)
and up to 65518 bytes of the message.

The six digits for a typed code: `HMAC-SHA256(key = handshake hash, "chamelab-sync/sas")`, the
first 4 bytes as a big-endian number modulo 1 000 000, shown as `123 456`.

## Data

### Operation

```json
{"author": "<device id>", "seq": 121, "time": "001763000000000-000000-<device id>",
 "type": "card", "id": "<entity id>",
 "set": {"name": "Office", "data": ["..."]},
 "base": {"name": "<device id>:57", "data": "<device id>:57"},
 "delete": true}
```

- `seq` counts the author's operations from 1; a device applies an author's operations in order.
- `time` is a hybrid logical clock: wall-clock milliseconds (15 digits), a counter (6 digits)
  and the author; it sorts as text. The node must equal `author`.
- `set` gives new values of fields; `base` names, per field, the operation whose value the author
  saw (for `delete`: every field of the entity). `set` and `delete` are optional.

### Entities and fields

| `type` | id | Fields |
| --- | --- | --- |
| `card` | the card id | every property of the card's JSON (as in a ChameLab card file) except `id` and `folder`; `folder` (a `cardFolder` id or `""`); `order` |
| `cardFolder` | random | `name`, `parent` (a `cardFolder` id or `""`) |
| `dictionary` | the dictionary id | every property of its JSON except `id` and `folder`; `folder` (a `dictionaryFolder` id or `""`); `order` |
| `dictionaryFolder` | random | `name`, `parent` |
| `member` | the device id | `name`, `key` (X25519 public key, b64), `added` (ISO 8601), `revoked` (the last `seq` accepted from it) |
| `group` | `group` | `room` (the room secret) |

`order` is a fractional index: a string over `0-9A-Za-z`, not ending in `0`; items sort by
`(order, id)` as ordinal strings.

### Merging

- **Fields**: the value with the greatest `time` wins.
- **Kept conflicts**: for `card.data` and `dictionary.keys`, every value no later operation names
  as its `base` is a head. While a field has more than one head, every head but the winner shows
  as a copy of the entity with the id `conflict-` + the first 24 hex digits of
  `SHA-256(utf8("<entity id>|<losing operation id>"))`: the entity's current values with that
  field set to the losing value (and `" (conflict)"` added to the name until someone renames it).
  Operations on the copy id override the derived values; deleting it settles the conflict.
- **Deletion**: the latest delete counts. The entity is deleted while every one of its fields
  still holds the value the delete named in its `base`; any other value (an edit the deleting
  device had not seen, or a later one) brings it back.
- **Members**: operations of a device whose `member.revoked` is set are refused past that `seq`.
- **Folders**: names that clash among siblings (ignoring case) get ` (2)`, ` (3)`… in id order; a
  folder whose parent is missing, or that is inside itself, is at the top.

### History

A device keeps the 10 most recent values of every field. "The entity right after time T" is,
per field, the latest kept value with `time ≤ T`.

### Snapshot

The whole state, for saving and for a peer that lacks operations this device no longer keeps:

```json
{"version": 1, "vector": {"<device id>": 121},
 "entities": [{"type": "card", "id": "...",
   "fields": {"name": {"versions": [{"op": "<id>:<seq>", "time": "...", "value": "...", "base": "<id>:<seq>"}],
                       "heads": [...], "superseded": ["<id>:<seq>"]}},
   "delete": {"op": "...", "time": "...", "base": {"name": "<id>:<seq>"}}}]}
```

Merging a snapshot merges every version, head, superseded id and deletion as if the operations
behind them were applied, and raises the vector to the snapshot's.

## Sync session (`p: sync`)

Both sides check that the other's static key is the `key` of a member that is not revoked, then:

| Message | Meaning |
| --- | --- |
| `{"type": "hello", "device": "<id>", "vector": {...}}` | sent first by both; the vector holds, per author, the last `seq` applied |
| `{"type": "ops", "ops": [...]}` | operations the other side lacks, oldest `time` first, up to 200 per message |
| `{"type": "snapshot", "state": {...}}` | sent before `ops` when some operations the other side lacks are no longer kept |
| `{"type": "want", "vector": {...}}` | an author's operations arrived with a gap: send again from this vector |

After the first exchange, each side pushes new operations (its own and those it received from
others) as they come. A device keeps one session per member: a local one replaces a relayed
one; of two of a kind, the one opened by the smaller device id stays.

## Adding a device (`p: join`)

The existing device opens a share on the relay (`/share`, the same as for a shared Chameleon)
and a TCP listener. It shows:

- the **typed code**: the relay's share code with the region, `DE1-ABCD-1234`;
- the **QR code**: `chamelab-sync:` + b64 of
  `{"v": 1, "code": "DE1-ABCD-1234", "key": b64(SHA-256(its public key)[:16]), "secret": b64(16 random bytes), "lan": ["192.168.1.5:41234"]}`.

The new device connects over the local network when the QR lists addresses and one answers
within 2 s, else through `/join/{code}`. With the QR it checks the key hash against the
handshake. It sends

```json
{"type": "join", "device": "<new id>", "name": "...", "key": "<its public key>", "proof": "b64(HMAC-SHA256(secret, handshake hash))", "confirmJoin": true}
```

(`proof` only with the QR). Without a valid proof both screens show the six digits. When
accepted, the existing device adds the `member` entity and answers
`{"type": "welcome", "device": "<its id>", "vector": {...}}` (or `{"type": "refused"}`); then the
channel carries a sync session. The new device takes the group's state up to that vector, then
adds its own library: items the group already has keep the group's version, folders with the same
path are the same folders.

Clients supporting completion confirmation include `confirmJoin: true` in `join`. The inviter
echoes it in `welcome` and keeps approval pending until the new device has received the initial
state, merged and saved its library, and sent `{"type": "joined"}` over the sync session.
On the joining device, cancellation wins until UI finalization starts. Once merging and saving
begin, finalization and the bounded acknowledgment send finish despite a late UI cancel, so a
delivered acknowledgment cannot leave the joining device outside the group. The inviter disables
its decision buttons while this completion is pending.
If the channel closes, either side cancels, or the five-minute confirmation timeout expires
before this acknowledgment, the inviter revokes the provisional member and rotates the room
secret. The joining device cancels its initial session and clears its group state on failure.
This prevents a canceled join from leaving a phantom member. A pairing channel awaiting the
acknowledgment is kept when another room or local connection arrives. Older peers that omit
`confirmJoin` retain the original exchange; new clients send `joined` only when it is echoed.

## Sending cards (`p: cards`)

The same rendezvous with a fresh key pair on both sides and no group: the receiver sends
`{"type": "receive", "name": "...", "proof": "..."}`, the sender (after the digits when there is
no proof) `{"type": "cards", "cards": [<card JSON>...]}`, and the receiver answers
`{"type": "thanks"}`. Received cards get new ids.

## Relay

`wss://<relay>/room/{room id}` (the room id matches `[A-Za-z0-9_-]{22,64}`), up to 16 sockets per
room. Binary frames:

- relay → device: `[kind: 1 byte][peer: u16 BE][payload]`: 1 = a packet from that peer, 2 = the
  peer joined (it opens the handshake), 3 = the peer left, 4 = the peer was there already (this
  device opens the handshake);
- device → relay: `[peer: u16 BE][payload]`, at most 128 KiB.

All devices pick the same relay: the relays ordered by
`hex(SHA-256(utf8("<room id>|<region>")))`, the first that answers `/healthz`.

## Local network

A node announces `_chamelab-sync._tcp.local` over mDNS / DNS-SD (PTR, SRV, TXT, A) with the TXT
values `v=1`, `n=b64(8 random bytes)` and `h=b64(HMAC-SHA256(room secret, n)[:12])`. A member
that recognises `h` connects over TCP and runs a sync session.

## Files

In the app's data folder (`IKeyValueStore`, plain JSON):

| File | Contents |
| --- | --- |
| `sync/device.json` | `{"version": 1, "device": "<id>", "name": "...", "key": "<private key, b64>"}` |
| `sync/snapshot.json` | the snapshot as of its last write |
| `sync/log.json` | operations since, one JSON object per line (a torn last line is dropped) |
| `sync/seen.json` | when each other device was last connected |
