# ChameLab.Relay

Lets someone share the Chameleon plugged into their computer with a ChameLab anywhere on the
internet. The same binary runs on several VPSes around the world, each known to ChameLab under a
region name (`TR1`, `BG1`, `DE1`, `RU1`, `US1`: country letters and a number).

- The sharing ChameLab times `/healthz` on every relay, opens `wss://<fastest>/share` and shows
  the code that relay hands out with the region in front: `TR1-ABCD-1234`.
- The connecting ChameLab finds the relay by the code's region and opens
  `wss://<relay>/join/ABCD1234`. The relay pairs the two and copies the device's bytes both
  ways; it never parses them. A relay knows nothing about regions or other relays.
- One connecting side per share at a time; it may leave and join again with the same code.
  The code dies when sharing stops.
- Codes are 8 Crockford base32 characters (40 bits); joins are limited to 10 per minute per IP.
- TLS: each relay serves HTTPS itself on port 7443 with a self-signed certificate, and ChameLab
  pins its SHA-256 (no domains or CA needed, port 443 stays free for whatever else runs there).
  The browser build can't pin, so it can't join shared devices; desktop and Android can.

The relay list with the pins is built into ChameLab (`RelayEndpoint.DefaultServers`). The `relayServers`
setting in `settings.json` replaces it, e.g. `{"XX1": "ws://127.0.0.1:5080"}` for a local test.

## Build

AOT for Linux compiles only on Linux, so the script runs the .NET SDK container (Docker
required):

```bash
./build.sh
```

The result is `publish/ChameLab.Relay`, one 13 MB binary that needs glibc 2.34+ (Ubuntu 22.04+).

## Deploy

```bash
deploy/deploy.sh user@host            # systemd service, Ubuntu 22.04+
deploy/deploy.sh root@host --docker   # container, for older hosts (the image brings its glibc)
```

The script copies the binary, makes `/etc/chamelab-relay/relay.{crt,key}` on the first run,
installs the service (or container), opens 7443 in ufw, checks `/healthz` and prints the
certificate's SHA-256. Put it into `RelayEndpoint.DefaultServers` with the server's address.
Later runs update the binary and keep the certificate: a new one would break the pin in every
released ChameLab. A provider-level firewall (e.g. a DigitalOcean Cloud Firewall) must allow
TCP 7443 too.

Logs: `journalctl -u chamelab-relay -f`, or `docker logs -f chamelab-relay`.
