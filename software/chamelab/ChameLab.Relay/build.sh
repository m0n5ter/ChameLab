#!/usr/bin/env bash
# Publishes the relay as a Native AOT linux-x64 binary inside the .NET SDK container
# (AOT for Linux can only be compiled on Linux). Output: ChameLab.Relay/publish/ChameLab.Relay
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(dirname "$here")"
mkdir -p "$here/publish"
# Docker Desktop on Windows needs Windows paths; Git Bash must not rewrite them.
host_path() { (cd "$1" && (pwd -W 2>/dev/null || pwd)); }
export MSYS_NO_PATHCONV=1

docker run --rm \
  -v "$(host_path "$root"):/src:ro" \
  -v "$(host_path "$here/publish"):/out" \
  mcr.microsoft.com/dotnet/sdk:10.0 bash -euo pipefail -c '
    apt-get update -qq && apt-get install -y -qq clang zlib1g-dev >/dev/null
    mkdir -p /work
    cp /src/Directory.Build.props /src/Directory.Packages.props /work/
    cp -r /src/ChameLab.Relay /work/
    rm -rf /work/ChameLab.Relay/bin /work/ChameLab.Relay/obj /work/ChameLab.Relay/publish
    dotnet publish /work/ChameLab.Relay -c Release -r linux-x64 -p:PublishAot=true -o /work/out
    cp /work/out/ChameLab.Relay /out/'

ls -l "$here/publish/ChameLab.Relay"
