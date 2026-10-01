#!/usr/bin/env bash
# Usage: tools/callers.sh 'ItemStack$$PopItemsInto'
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd -W)"
source "$(dirname "$0")/local.env"
mkdir -p "$(dirname "$0")/../research/decomp"
OUT="$ROOT/research/decomp/${GVER:+$GVER/}callers_$(echo "$1" | tr -c 'A-Za-z0-9_\n' '_').txt"

"$GHIDRA" "$PROJ_DIR" "Nivalis${GVER:+/$GVER}" -process GameAssembly.dll -noanalysis -readOnly \
  -scriptPath "$ROOT/tools/ghidra_scripts" \
  -postScript FindCallers.java "$OUT" "$1" > /dev/null 2>&1
cat "$OUT"