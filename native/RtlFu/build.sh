#!/usr/bin/env bash
# Verilates an RTL unit and links it with an FFI shim into a native shared
# library that the C# side (Mechanism/RtlFu) can load at runtime.
#
# Usage: build.sh <verilog-file> <top-module> <output-shared-library-path> [shim.cpp]
#
# The shim defaults to rtl_fu_shim.cpp (functional units; RtlFfiFunctionalUnit).
# Pass rtl_bp_shim.cpp for branch predictors (RtlFfiBranchPredictor).
#
# Examples:
#   native/RtlFu/build.sh native/RtlFu/generated/DivUnit.sv DivUnit /tmp/rtl_div.so
#   native/RtlFu/build.sh native/RtlFu/generated/GshareBp.sv GshareBp /tmp/rtl_gshare.so rtl_bp_shim.cpp
#
# Requires verilator + g++. On NixOS, re-execs itself under nix-shell when
# verilator is not already on PATH.

set -euo pipefail

if [[ $# -lt 3 || $# -gt 4 ]]; then
    echo "Usage: $0 <verilog-file> <top-module> <output-shared-library-path> [shim.cpp]" >&2
    exit 1
fi

if ! command -v verilator >/dev/null 2>&1; then
    if command -v nix-shell >/dev/null 2>&1; then
        exec nix-shell -p verilator python3 --run "$(printf '%q ' "$0" "$@")"
    fi
    echo "verilator not found on PATH and nix-shell unavailable" >&2
    exit 1
fi

SV_FILE="$(realpath "$1")"
TOP="$2"
OUTPUT="$3"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SHIM="$SCRIPT_DIR/${4:-rtl_fu_shim.cpp}"
OBJ_DIR="$(mktemp -d)"
trap 'rm -rf "$OBJ_DIR"' EXIT

verilator --cc "$SV_FILE" --top-module "$TOP" \
    -Mdir "$OBJ_DIR" -CFLAGS "-fPIC -O2" --build -j 0 --quiet

VERILATOR_ROOT="$(verilator --getenv VERILATOR_ROOT)"

g++ -O2 -fPIC -shared -std=c++17 -pthread \
    -I "$OBJ_DIR" \
    -I "$VERILATOR_ROOT/include" \
    -I "$VERILATOR_ROOT/include/vltstd" \
    -DRTL_MODEL_HEADER="\"V${TOP}.h\"" \
    -DRTL_MODEL="V${TOP}" \
    "$SHIM" \
    "$OBJ_DIR"/*.o \
    -o "$OUTPUT"

echo "Built $OUTPUT"
