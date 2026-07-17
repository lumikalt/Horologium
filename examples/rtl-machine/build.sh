#!/usr/bin/env bash
# Verilates the example units into the shared libraries machine.fsx expects.
# Reuses the repo's build script and FFI shims (native/RtlFu).

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/../.." && pwd)"

"$REPO/native/RtlFu/build.sh" "$SCRIPT_DIR/generated/MyAlu.sv" MyAlu /tmp/rtl_example_alu.so
"$REPO/native/RtlFu/build.sh" "$SCRIPT_DIR/generated/MyBp.sv" MyBp /tmp/rtl_example_bp.so rtl_bp_shim.cpp
