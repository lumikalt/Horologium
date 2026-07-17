#!/usr/bin/env bash
# Regenerates generated/*.sv from the Chisel sources (Chisel → firtool → SystemVerilog).
#
# Needs a JVM + scala-cli + firtool; on NixOS the nix-shell below provides all three.
# The generated Verilog is committed, so this only needs to run after editing the
# Chisel source — building the Verilator shims (build.sh) does not require it.

set -euo pipefail
cd "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

nix-shell -p scala-cli circt --run '
    export CHISEL_FIRTOOL_PATH="$(dirname "$(command -v firtool)")"
    scala-cli run . --main-class rtlfu.Generate
    scala-cli run . --main-class rtlfu.GenerateBp
    scala-cli run . --main-class rtlfu.GenerateRp
    scala-cli run . --main-class rtlfu.GeneratePf
    scala-cli run . --main-class rtlfu.GenerateLTage
    scala-cli run . --main-class rtlfu.GenerateDrrip
    scala-cli run . --main-class rtlfu.GenerateStream
    scala-cli run . --main-class rtlfu.GenerateMul
'

echo "Regenerated generated/*.sv"
