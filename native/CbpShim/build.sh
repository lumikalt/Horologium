#!/usr/bin/env bash
# Compiles a CBP-3/CBP-5-style `class PREDICTOR` submission header into a native
# shared library that CbpFfiPredictor (C#) can load at runtime.
#
# Usage: build.sh <predictor-header-path> <output-shared-library-path>
#
# Example:
#   native/CbpShim/build.sh third_party/tage_sc_l/predictor.h /tmp/tage_sc_l.so

set -euo pipefail

if [[ $# -ne 2 ]]; then
    echo "Usage: $0 <predictor-header-path> <output-shared-library-path>" >&2
    exit 1
fi

PREDICTOR_HEADER="$(realpath "$1")"
OUTPUT="$2"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

g++ -O2 -fPIC -shared -std=c++17 \
    -I "$SCRIPT_DIR" \
    -DCBP_PREDICTOR_HEADER="\"$PREDICTOR_HEADER\"" \
    "$SCRIPT_DIR/cbp_shim.cpp" \
    -o "$OUTPUT"

echo "Built $OUTPUT"
