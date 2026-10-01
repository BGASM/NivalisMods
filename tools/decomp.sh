#!/usr/bin/env bash
# Usage: tools/decomp.sh 'VenueAreaGhost$$TryPurchaseIngredients' 'Vendor$$BuyItem' ...
# Ghidra GUI must be closed. GVER=v1.0-p1 uses the patched build (project folder and output subfolder).
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd -W)"
source "$(dirname "$0")/local.env"

"$GHIDRA" "$PROJ_DIR" "Nivalis${GVER:+/$GVER}" -process GameAssembly.dll -noanalysis -readOnly \
  -scriptPath "$ROOT/tools/ghidra_scripts" \
  -postScript DecompileByName.java "$ROOT/research/decomp${GVER:+/$GVER}${GOUT:+/$GOUT}" "$@" 2>&1 | grep -E "wrote|ERROR|Exception" || true