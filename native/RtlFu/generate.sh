#!/usr/bin/env bash
# Regenerates generated/DivUnit.sv from DivUnit.scala (Chisel → firtool → SystemVerilog).
#
# Needs a JVM + scala-cli + firtool; on NixOS the nix-shell below provides all three.
# The generated Verilog is committed, so this only needs to run after editing the
# Chisel source — building the Verilator shim (build.sh) does not require it.

set -euo pipefail
cd "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

nix-shell -p scala-cli circt --run '
    CHISEL_FIRTOOL_PATH="$(dirname "$(command -v firtool)")" \
    scala-cli run DivUnit.scala
'

echo "Regenerated generated/DivUnit.sv"
