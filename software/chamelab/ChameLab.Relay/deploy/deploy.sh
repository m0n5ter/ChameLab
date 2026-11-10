#!/usr/bin/env bash
# Installs or updates the relay on one server over SSH and prints its certificate fingerprint,
# which goes into RelayEndpoint.DefaultServers.
#
#   deploy/deploy.sh user@host            systemd service (Ubuntu 24.04+)
#   deploy/deploy.sh user@host --docker   container (older hosts; Docker required)
#
# Run ../build.sh first. The certificate is made once per server and kept on later updates:
# a new one would break the pin in every ChameLab already released.
set -euo pipefail

target="$1"
mode="${2:-}"
here="$(cd "$(dirname "$0")" && pwd)"
binary="$here/../publish/ChameLab.Relay"
[ -f "$binary" ] || { echo "No $binary: run build.sh first." >&2; exit 1; }

stage="/tmp/chamelab-relay-deploy"
ssh "$target" "rm -rf $stage && mkdir -p $stage"
scp -q "$binary" "$here/chamelab-relay.service" "$here/Dockerfile" "$target:$stage/"

ssh "$target" "MODE='$mode' STAGE='$stage' bash -s" <<'REMOTE'
set -euo pipefail
S=""; [ "$(id -u)" = 0 ] || S="sudo"

# Self-signed certificate, made once.
$S mkdir -p /etc/chamelab-relay
if ! $S test -f /etc/chamelab-relay/relay.crt; then
  $S openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 7300 \
    -subj /CN=chamelab-relay -keyout /etc/chamelab-relay/relay.key -out /etc/chamelab-relay/relay.crt 2>/dev/null
fi
$S chmod 600 /etc/chamelab-relay/relay.key

chmod 755 "$STAGE/ChameLab.Relay"  # scp from Windows drops the executable bit
if [ "$MODE" = "--docker" ]; then
  # The chiseled image runs as uid 1654.
  $S chown 1654:1654 /etc/chamelab-relay/relay.key
  $S docker build -q -t chamelab-relay "$STAGE" >/dev/null
  $S docker rm -f chamelab-relay >/dev/null 2>&1 || true
  $S docker run -d --name chamelab-relay --restart unless-stopped --memory 64m \
    -p 7443:7443 -v /etc/chamelab-relay:/certs:ro chamelab-relay >/dev/null
else
  $S chown root:root /etc/chamelab-relay/relay.key
  $S install -D -m 755 "$STAGE/ChameLab.Relay" /opt/chamelab-relay/ChameLab.Relay
  $S install -m 644 "$STAGE/chamelab-relay.service" /etc/systemd/system/chamelab-relay.service
  $S systemctl daemon-reload
  $S systemctl enable chamelab-relay >/dev/null 2>&1
  $S systemctl restart chamelab-relay
fi

if $S ufw status 2>/dev/null | grep -q "Status: active"; then
  $S ufw allow 7443/tcp comment "ChameLab relay" >/dev/null
fi
rm -rf "$STAGE"

health="FAILED"
for _ in $(seq 20); do
  if curl -sk --max-time 2 https://127.0.0.1:7443/healthz | grep -q ok; then health="ok"; break; fi
  sleep 0.5
done
echo "healthz: $health"
$S openssl x509 -in /etc/chamelab-relay/relay.crt -noout -fingerprint -sha256 | sed 's/.*=//; s/://g'
REMOTE
