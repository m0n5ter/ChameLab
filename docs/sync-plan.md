# Device sync: design and plan

Status: implemented on the `VK_Sync` branch; the wire formats are in [sync-protocol.md](sync-protocol.md).

ChameLab keeps the card library, its folders and the key dictionaries in step across a user's
devices and lets people send cards to each other. The data and its history live **only on the
user's devices**, as plain JSON files. Servers are the existing stateless relays
(`ChameLab.Relay`): they bring devices together while both are online and copy encrypted
bytes, nothing more.

## Goals

- Every device of a user ends up with the same library, without accounts, passwords or a
  phrase to write down.
- No server stores data, history or metadata beyond the lifetime of a connection.
- Edits on several devices while offline merge per field. Two edits of the same dump on
  different devices both survive (one as a conflict copy) and are never dropped silently.
- History: every card, folder and dictionary can be rolled back, and deleted ones restored.
- Sending a card or folder to someone else in a couple of taps.
- Desktop, Android, iOS and the CLI; the browser build as a later phase (see
  [Open questions](#open-questions)).

Non-goals: sync with Chameleon Ultra GUI's cloud (import and export of its files stay as they
are), syncing settings (maybe later, as a separate entity type), real-time collaboration,
recovering data after every device of a group is lost (that is what backups are for).

## Overview

```
 phone ──┐                        ┌── desktop (background node, always on)
         │   wss://relay/room/R   │
 laptop ─┼──────── relay ─────────┼── headless node (`chamelab sync serve`, Pi / NAS / VPS)
         │  (copies frames only)  │
 tablet ─┘                        └── ...
```

- A **group** is the set of one user's devices. Each device keeps the whole **operation log**
  of the group (who changed what, when) and the library built from it.
- All devices of a group meet in one **room** on a relay. The room id is a secret only members
  know; the relay keeps a room only while someone is connected to it.
- In the room, every pair of devices runs an authenticated, encrypted channel (Noise) through
  the relay, swaps version vectors and sends each other the operations the other one lacks.
  While both stay connected, new edits are pushed right away.
- **Desktop builds are servers**: in background mode they stay in the room while the computer
  is on, so a phone opening ChameLab finds a peer that has everything. A **headless node** (the
  CLI in Docker) does the same on hardware that never sleeps. Operations travel through any
  device, so two devices never online at the same time still sync through a third one.
- Every node is a full member: it has all the data and can read and edit it.

## Cryptography

Only the connections are encrypted; data at rest is plain JSON like the library today. All
primitives come from BouncyCastle (already a dependency), so the same code runs on every
platform including WebAssembly.

| Use | Primitive |
| --- | --- |
| Channel | Noise `XX_25519_ChaChaPoly_SHA256` for every connection; members check each other's static key against the member list |
| Device identity | X25519 static key per device, generated locally, never leaves it |
| Derivation | HKDF-SHA256 |

- The **room secret** (32 random bytes) gives the room id
  (`HKDF(secret, "chamelab-sync/room/v1")`). Knowing it only lets someone into the room on the
  relay; peers still refuse any key that is not in the member list.
- Revoking a device makes a new room secret, sent to the remaining members only, so the
  revoked device cannot even find the group any more.

## Data model

Entities: `card`, `cardFolder`, `dictionary`, `dictionaryFolder`. Each has a stable id and a
set of fields; an operation changes some fields of one entity, or deletes it.

| Entity | Fields (each merged on its own) |
| --- | --- |
| `card` | `name`, `color`, `folder`, `order`, `identity` (tag, UID, SAK, ATQA, ATS together), `data` (the dump), `extra` |
| `cardFolder` | `name`, `parent`, `order` |
| `dictionary` | `name`, `color`, `folder`, `type`, `keys` |
| `dictionaryFolder` | `name`, `parent`, `order` |

- **Folders are entities with ids** in the sync data. A folder in the library is a path stored
  on each card, so a rename rewrites every card in it and would race with a move made on
  another device; in sync a rename is one operation on the folder. The library keeps its paths
  and the UI stays the same: sync maps paths to folder ids (`LibrarySync`). Nesting (`parent`) is supported by the format, so
  Chameleon Ultra GUI's nested folders can be kept later without another migration.
- **Order** is a fractional index string (sorts between its neighbours), so a reorder changes
  one card, not the whole list.

### Operation

```json
{"author": "<device id>", "seq": 121, "hlc": "000001763000000-000000-<device>",
 "entity": "card", "id": "<card id>",
 "set": {"name": {"value": "Office", "base": "<device id>:57"}},
 "delete": false}
```

- `seq` per author gives each device a **version vector** (`{A: 120, B: 45}`); syncing is
  "here is my vector, send me what I lack".
- **Merge**: per field, the highest HLC wins. An op also names the op each field was based on
  (`base`). When a `data` (dump) or `keys` change arrives whose base is not the current value,
  the two edits were concurrent: the winner stays, the other one becomes a copy
  ("Card (conflict)"). Other fields simply resolve by HLC.
- **Delete** is a tombstone. An edit concurrent with a delete revives the entity (no edit is
  lost).
- **History** is the log itself: rolling back writes a new op with old values.
- **Compaction**: devices gossip the vectors they have seen from others. Ops seen by every
  member fold into a snapshot, except the last 10 versions of each entity, which stay for
  history. A new device receives the latest snapshot plus the log after it.

### Membership

Membership changes are operations too (entity `member`), made by any member:

- `add`: device id, name, X25519 public key.
- `revoke`: device id and the vector of its ops accepted so far; later ops by that device are
  rejected. The member that revokes sends the new room secret to the others over their
  channels. Data the device already has stays on it, as with any scheme.
- `rename`: a device's display name.

### Local storage

Plain JSON files under the existing per-user data folder (`IKeyValueStore`):

| File | Contents |
| --- | --- |
| `cards`, `dictionaries`, … | the library, as today (with folder ids and order added) |
| `sync/log.json` | the operation log, one op per line (JSON Lines), appended; a torn last line after a crash is dropped |
| `sync/snapshot.json` | the compacted state the log continues from |
| `sync/state.json` | own device id and keys, room secret, members, vectors seen from others |

The library stays the source the UI works with: local edits are turned into ops by diffing old
and new entities, remote ops are applied to the library without echoing back. The browser build
would keep the same files in its existing store later.

## Relay

The relay keeps its job of pairing sockets and copying bytes it does not understand; it stays
stateless. New:

- `wss://relay/room/{roomId}`: a room with up to 16 sockets. The relay numbers the sockets in
  a room and wraps every frame with a 2-byte peer number (`from` on the way out, `to` on the
  way in), and tells everyone in the room when a peer joins or leaves. The room disappears with
  its last socket.
- Pairing and card sending reuse the existing short codes (`DE1-ABCD-1234`) for one-time rooms
  of two.
- Limits: sockets per IP, rooms per IP, frame size, bytes per minute per socket; idle sockets
  get WebSocket pings as today.
- **Which relay**: all devices of a group must reach the same one. Each device orders the
  relays by rendezvous hashing of the room id, so they all agree on the order without talking,
  and connects to the first one that answers `/healthz`. A group can also fix a relay at
  creation (closest to the user) and fall back to that order.

## Flows

**Create a group.** Settings → Sync → Turn on. ChameLab makes the room secret and the device
key, and on desktop offers to keep syncing in the background (checked by default).

**Add a device.** On a member: "Add device" shows a QR and a code. On the new device: scan or
type it. Both run Noise `XX` in a one-time room; a QR also carries a hash of the inviter's key,
so scanning needs no further check, while a typed code shows six digits on both screens to
compare. The member writes the `add` op and hands over the room secret, the member list and its
vector; the new device then syncs the log as usual.

**Send a card or folder.** "Send…" on a card or folder shows a code / QR; the other person
opens it in ChameLab (any ChameLab, no group needed), sees what is offered and accepts. Bytes
go through a one-time room over Noise `XX`; nothing is stored anywhere. The received cards are
new entities in the receiver's library.

**Revoke a device.** Devices list → Remove: writes `revoke` and moves the group to a new room.
Removing the current device leaves the group and deletes its sync files (the library stays).

**Backups.** Losing every device of a group loses its data. The existing library export
covers that; the background node can also export the library to a folder of the user's choice
on a schedule (for example a folder another cloud already syncs).

## Local network

Devices on the same network also sync without the internet:

- A node that can accept connections (desktop, Android and iOS while open, the headless node)
  listens on a TCP port and announces itself over mDNS / DNS-SD as `_chamelab-sync._tcp`. The
  announcement carries `HMAC(room secret, nonce)` and the nonce, not the room id, so members
  recognise their group while other people on the network cannot tell announcements of one
  group apart.
- The channel is the same Noise `XX` with the member keys and the same exchange as through the
  relay; the local network is not trusted more than the relay.
- A device reachable both ways keeps one link: links to the same member are deduplicated by
  device id, the local one preferred.
- Adding a device without the internet: the QR also carries the inviter's local addresses and
  port, so the new device can connect directly. A typed code still goes through the relay.
- Platforms: Windows asks for the firewall once (MSIX needs the `privateNetworkClientServer`
  capability); Android takes a `MulticastLock` (`CHANGE_WIFI_MULTICAST_STATE`); iOS announces
  and browses through the system Bonjour (`NSLocalNetworkUsageDescription`,
  `NSBonjourServices`) and only while in the foreground; the browser build cannot do it.

## Background node (desktop)

- Tray icon (Avalonia `TrayIcon`); closing the window keeps sync running when background mode
  is on; Quit in the tray menu ends it.
- Start with the system: Windows `Run` registry value, or a `StartupTask` for MSIX; macOS
  LaunchAgent; Linux `~/.config/autostart/*.desktop`.
- Stays in the room, reconnects with backoff after network changes, sleep and relay restarts.
  It does not keep the computer awake.

## Headless node (CLI)

- `chamelab sync serve --join <code>` joins a group as a member and stays in its room, with its
  own copy of the library on disk.
- A Docker image next to the relay's, for a Raspberry Pi, a NAS or a VPS.

## Mobile

Android and iOS connect while ChameLab is in the foreground: on start, on resume and after
local edits, and leave the room when sent to the background. A background node of the same
group makes this enough in practice.

## Phases

Each phase ends with its tests; nothing user-visible ships before phase 6.

1. **Protocol spec**: `docs/sync-protocol.md` with the wire formats above, written before
   code and kept as the contract.
2. **Crypto** (`ChameLab.Core/Sync`): X25519, HKDF, Noise `XX` on BouncyCastle.
   Tests against published vectors (RFC 7748, RFC 5869, Noise cacophony vectors).
3. **Log and merge engine** (`ChameLab.Core/Sync`), in memory: ops, vectors, per-field merge,
   conflict copies, tombstones, membership, compaction and snapshots. Randomized tests: many
   devices, random edits, random delivery order and duplication → all converge; revoked
   devices' later ops rejected.
4. **Library integration**: folder entities with ids and migration of existing libraries,
   fractional order, diffing library changes into ops, applying remote ops, the JSON sync
   files. Tests on migration, round trips and a torn log line.
5. **Relay rooms**: `/room/{id}` with peer numbers and join/leave events, limits; one-time
   rooms by code. Tests in `ChameLab.Tests/RelayTests.cs` style; deploy to all relays before
   clients use it.
6. **Sync sessions and membership**: per-peer Noise channel through the room, vector exchange,
   catch-up and live push, reconnects; create group, add device, revoke. End-to-end tests with
   several in-process nodes and a local relay.
7. **Local network**: TCP listener and connector, mDNS / DNS-SD announce and browse, the
   iOS Bonjour and Android multicast pieces, local addresses in the pairing QR.
8. **UI**: Settings → Sync (on/off, devices with last seen, add device QR/code, remove), sync
   status in the toolbar, card / dictionary history with restore, Send card / folder and the
   receive dialog. Localized strings for every language.
9. **Desktop background node**: tray, close to tray, autostart per OS, scheduled export.
10. **Headless node**: `chamelab sync serve`, Docker image and its docs.
11. **Release**: user docs, website, `CHANGELOG.md` and `CHANGELOG.ru.md`.

Later: browser build (a relay the browser can trust), syncing selected settings.

## Open questions

- **Browser build**: it cannot pin the relays' self-signed certificates. Sync there needs at
  least one relay behind a domain with a CA certificate.
- **Relay load**: background nodes keep one idle socket each all day. Fine for the current
  VPSes at expected numbers, but `MaxSessions` and memory per socket need checking.
- **Room size**: 16 sockets per group is generous for one user's devices.
