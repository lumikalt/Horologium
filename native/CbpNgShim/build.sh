#!/usr/bin/env bash
# Compiles a CBP2025/CBP-NG (AmpereComputing/cbp-ng) predictor submission header into a
# native shared library that CbpNgFfiPredictor (C#) can load at runtime.
#
# Unlike the CBP-3/CBP-5 shim (native/CbpShim/), cbp-ng predictors are templated structs
# named after the technique (e.g. `bimodal<6,17,12>` in predictors/bimodal.hpp), not a
# fixed `class PREDICTOR`, and they #include "../cbp.hpp" / "../harcom.hpp" via paths
# relative to their own location in a cbp-ng checkout. This script stages a temporary
# tree — this shim's own cbp.hpp (see its header comment for why it's a reimplementation,
# not the vendored original) + vendor/harcom.hpp + a copy of the user's predictor header —
# so those relative includes resolve correctly, then compiles cbp_ng_shim.cpp against it.
#
# Usage: build.sh <predictor-header-path> <predictor-type-expr> <output-shared-library-path>
#
# Example (against a local AmpereComputing/cbp-ng checkout):
#   native/CbpNgShim/build.sh \
#       ~/cbp-ng/predictors/bimodal.hpp "bimodal<6,17,12>" /tmp/bimodal.so

set -euo pipefail

if [[ $# -ne 3 ]]; then
    echo "Usage: $0 <predictor-header-path> <predictor-type-expr> <output-shared-library-path>" >&2
    exit 1
fi

PREDICTOR_HEADER="$(realpath "$1")"
PREDICTOR_TYPE="$2"
OUTPUT="$3"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

# cbp_ng_shim.cpp is staged alongside cbp.hpp too (rather than compiled from
# $SCRIPT_DIR with just -I "$STAGE") so its own `#include "cbp.hpp"` resolves to the
# exact same file predictors/predictor.hpp's `#include "../cbp.hpp"` resolves to —
# otherwise quote-include's "search the including file's own directory first" rule
# picks up the repo's cbp.hpp for one and the staged copy for the other, and the
# resulting duplicate (but distinct-path) definitions of `predictor`/`instruction_info`
# fail to compile despite both files having #pragma once.
cp "$SCRIPT_DIR/cbp.hpp" "$STAGE/cbp.hpp"
cp "$SCRIPT_DIR/cbp_ng_shim.cpp" "$STAGE/cbp_ng_shim.cpp"
cp "$SCRIPT_DIR/vendor/harcom.hpp" "$STAGE/harcom.hpp"
mkdir -p "$STAGE/predictors"
cp "$PREDICTOR_HEADER" "$STAGE/predictors/predictor.hpp"

# -fvisibility=hidden: harcom.hpp declares its `panel` state as a C++17 inline
# global. Inline globals get merged by the dynamic linker across every shared
# library that defines them with default visibility loaded into the same
# process — so without this flag, destroying one predictor instance (which
# runs harcom's reg/ram destructors, setting panel.storage_destroyed) corrupts
# every other loaded CbpNgShim .so, even ones built from a different predictor
# header. Hidden visibility gives each .so its own private copy. The four
# extern "C" entry points are given default visibility explicitly so dlsym
# can still find them.
g++ -O2 -fPIC -shared -std=c++20 -fvisibility=hidden -fvisibility-inlines-hidden \
    -DCHEATING_MODE -DFREE_FANOUT \
    -DCBPNG_PREDICTOR_HEADER="\"predictors/predictor.hpp\"" \
    -DCBPNG_PREDICTOR_TYPE="$PREDICTOR_TYPE" \
    "$STAGE/cbp_ng_shim.cpp" \
    -o "$OUTPUT"

echo "Built $OUTPUT"
