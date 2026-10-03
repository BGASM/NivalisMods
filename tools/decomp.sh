#!/usr/bin/env bash
# Usage: tools/decomp.sh 'VenueAreaGhost$$TryPurchaseIngredients' 'Vendor$$BuyItem' ...
# Ghidra GUI must be closed. Decompiles the current build (patch 2) into research/decomp/v1.0-p2;
# GVER=v1.0-p1 or GVER=v1.0 for earlier builds.
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd -W)"
source "$(dirname "$0")/local.env"
GVER="${GVER-v1.0-p2}"   # current game build; GVER=v1.0-p1 or v1.0 for earlier ones
[ "$GVER" = "v1.0" ] && GFOLDER="Nivalis" || GFOLDER="Nivalis/$GVER"

"$GHIDRA" "$PROJ_DIR" "$GFOLDER" -process GameAssembly.dll -noanalysis -readOnly \
  -scriptPath "$ROOT/tools/ghidra_scripts" \
  -postScript DecompileByName.java "$ROOT/research/decomp/$GVER" "$@" 2>&1 | grep -E "wrote|ERROR|Exception" || true