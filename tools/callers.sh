#!/usr/bin/env bash
# Usage: tools/callers.sh 'ItemStack$$PopItemsInto'
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd -W)"
source "$(dirname "$0")/local.env"
GVER="${GVER-v1.0-p1}"   # current game build; GVER=v1.0 for the original release
[ "$GVER" = "v1.0" ] && GFOLDER="Nivalis" || GFOLDER="Nivalis/$GVER"
mkdir -p "$(dirname "$0")/../research/decomp/$GVER"
OUT="$ROOT/research/decomp/$GVER/callers_$(echo "$1" | tr -c 'A-Za-z0-9_\n' '_').txt"

"$GHIDRA" "$PROJ_DIR" "$GFOLDER" -process GameAssembly.dll -noanalysis -readOnly \
  -scriptPath "$ROOT/tools/ghidra_scripts" \
  -postScript FindCallers.java "$OUT" "$1" > /dev/null 2>&1
cat "$OUT"