#!/usr/bin/env bash
# Usage: tools/decomp.sh 'VenueAreaGhost$$TryPurchaseIngredients' 'Vendor$$BuyItem' ...
# Ghidra GUI must be closed.
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd -W)"
source "$(dirname "$0")/local.env"

"$GHIDRA" "$PROJ_DIR" Nivalis -process GameAssembly.dll -noanalysis -readOnly \
  -scriptPath "$ROOT/tools/ghidra_scripts" \
  -postScript DecompileByName.java "$ROOT/research/decomp" "$@" 2>&1 | grep -E "wrote|ERROR|Exception" || true