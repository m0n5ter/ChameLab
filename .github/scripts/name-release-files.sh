#!/usr/bin/env bash
# Puts the versions into the release file names: the app version into the app and CLI
# packages, the firmware version into the firmware packages.
#   chamelab-win-x64.zip -> chamelab-1.0.7-win-x64.zip, ultra-dfu-app.zip -> ultra-dfu-app-2.3.2-cl.zip
# Usage: name-release-files.sh <dir> <app version> <firmware version>
set -euo pipefail
dir=$1 app=$2 fw=$3
cd "$dir"
for f in *; do
  case $f in
    chamelab-* | client-*) to="${f%%-*}-$app-${f#*-}" ;;
    *-dfu-* | *-binaries.*) to="${f%.*}-$fw.${f##*.}" ;;
    *) continue ;;
  esac
  mv -- "$f" "$to"
done
ls -l
