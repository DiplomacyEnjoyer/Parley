#!/usr/bin/env bash
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
#
# Downloads third-party maps and board pictures at setup time and installs the classic
# board for the web client. Nothing it downloads is committed (see THIRD_PARTY.md).
#
#   third_party/maps/dpjudge/   DPjudge-format .map files from the Python "diplomacy"
#                               package (AGPL-3.0). Used by the rule tests (DATC) and
#                               the importer.
#   third_party/svg/diplomacy/  board pictures from the same package (jDip artwork, GPL).
#   maps-local/standard.pmap    the classic board, converted for Parley
#   maps-local/standard.art.json  its picture, matched province by province
#
# Usage: ./scripts/fetch-reference-maps.sh          (needs internet, Python 3 with pip, .NET 8)
#        ./scripts/fetch-reference-maps.sh --no-install   (download only; skip the conversion)
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION=1.1.2
DEST=third_party/maps/dpjudge
SVGDEST=third_party/svg/diplomacy
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

echo "Downloading the Python diplomacy package $VERSION (maps and board pictures)..."
python3 -m pip download "diplomacy==$VERSION" --no-deps --no-binary :all: -d "$TMP" -q
mkdir -p "$TMP/x" "$DEST" "$SVGDEST"
tar -xzf "$TMP"/diplomacy-*.tar.gz -C "$TMP/x"
cp "$TMP"/x/diplomacy-*/diplomacy/maps/*.map "$DEST"/
cp "$TMP"/x/diplomacy-*/LICENSE "$DEST"/LICENSE.diplomacy-package.txt
cp "$TMP"/x/diplomacy-*/diplomacy/maps/svg/*.svg "$SVGDEST"/
cp "$TMP"/x/diplomacy-*/LICENSE "$SVGDEST"/LICENSE.diplomacy-package.txt
echo "Fetched $(ls "$DEST"/*.map | wc -l) maps into $DEST and $(ls "$SVGDEST"/*.svg | wc -l) board pictures into $SVGDEST"

if [ "${1:-}" = "--no-install" ]; then exit 0; fi

# Install the classic board (and the other maps that come with a picture) for the web client.
mkdir -p maps-local
TOOL="dotnet run --project tools/Parley.Tools --"
for m in standard modern ancmed; do
  [ -f "$DEST/$m.map" ] || continue
  $TOOL import-dpjudge "$DEST/$m.map" "maps-local/$m.pmap" | grep -v '^warning' || true
  if [ -f "$SVGDEST/$m.svg" ]; then
    $TOOL import-svg "maps-local/$m.pmap" "$SVGDEST/$m.svg" "maps-local/$m.art.json" "$DEST/$m.map" | grep -v '^warning' || true
  fi
done
echo "Installed into maps-local/. Start the web client and the classic board is the default map."
