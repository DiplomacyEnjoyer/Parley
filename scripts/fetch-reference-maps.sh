#!/usr/bin/env bash
# Downloads third-party reference map files into third_party/ (which git ignores).
#
# Parley does not ship the classic board or other people's variant maps. The rule
# test suite (DATC) is written for the classic board, so the tests need its
# connection data from somewhere: this script takes the DPjudge-format map files
# that ship inside the open-source Python "diplomacy" package (AGPL-3.0) on PyPI.
# They are used locally as test input and are not redistributed by this repository.
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION=1.1.2
DEST=third_party/maps/dpjudge
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
python3 -m pip download "diplomacy==$VERSION" --no-deps --no-binary :all: -d "$TMP" -q
mkdir -p "$TMP/x" "$DEST"
tar -xzf "$TMP"/diplomacy-*.tar.gz -C "$TMP/x"
cp "$TMP"/x/diplomacy-*/diplomacy/maps/*.map "$DEST"/
cp "$TMP"/x/diplomacy-*/LICENSE "$DEST"/LICENSE.diplomacy-package.txt
echo "Fetched $(ls "$DEST"/*.map | wc -l) reference maps into $DEST"
