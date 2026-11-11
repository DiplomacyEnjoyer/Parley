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
#   third_party/jdip/           variant packs from jDip 1.6.8 (SourceForge; GPL): XML map
#                               data plus a board picture per variant
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

JDIP=third_party/jdip
JDIP_URL="https://sourceforge.net/projects/jdip/files/jdip/jdip%201.6.8/jdip-1.6.8-bin-noinstaller.zip/download"
if [ ! -f "$JDIP/stdVariants.zip" ]; then
  echo "Downloading jDip 1.6.8 variant packs..."
  mkdir -p "$JDIP"
  if curl -fsSL -o "$TMP/jdip.zip" "$JDIP_URL"; then
    python3 - "$TMP/jdip.zip" "$JDIP" <<'PY'
import sys, zipfile, os
z = zipfile.ZipFile(sys.argv[1])
for n in z.namelist():
    base = os.path.basename(n)
    if n.startswith("jdip/variants/") and base.endswith(".zip"):
        open(os.path.join(sys.argv[2], base), "wb").write(z.read(n))
    if base.lower() in ("license.txt", "copying", "license"):
        open(os.path.join(sys.argv[2], "LICENSE.jdip.txt"), "wb").write(z.read(n))
PY
    echo "Fetched $(ls "$JDIP"/*.zip | wc -l) jDip variant packs into $JDIP"
  else
    echo "Could not download jDip from SourceForge; the jDip variants are skipped."
  fi
fi

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
# Community variants from jDip (each with its picture). See docs/variants-research.md.
if [ -f "$JDIP/stdVariants.zip" ]; then
  while IFS='|' read -r pack variant id; do
    [ -f "$JDIP/$pack" ] || continue
    $TOOL import-jdip "$JDIP/$pack" "$variant" "maps-local/$id.pmap" "$id" | grep -E '^wrote|not load' || true
  done <<'LIST'
hundredVariants.zip|Hundred3 v2 [H32]|hundred
youngstownVariants.zip|Youngstown|youngstown
sailhoVariants.zip|Sail Ho!|sailho
stdVariants.zip|1898|v1898
stdVariants.zip|Fleet Rome|fleetrome
milanVariants.zip|Milan|milan
asiaVariants.zip|Asia|asia
loeb9Variants.zip|Loeb9|loeb9
rootzVariants.zip|Root Z|rootz
LIST
fi
echo "Installed into maps-local/. Start the web client and the classic board is the default map."
