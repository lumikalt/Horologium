#!/usr/bin/env bash
# Regenerates generated/*.sv from the example Chisel sources (Chisel → firtool → SV).
# Needs a JVM + scala-cli + firtool; the nix-shell below provides all three.
# The generated Verilog is committed, so this only runs after editing the Chisel.

set -euo pipefail
cd "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

nix-shell -p scala-cli circt --run '
    export CHISEL_FIRTOOL_PATH="$(dirname "$(command -v firtool)")"
    scala-cli run . --main-class examplertl.GenerateAlu
    scala-cli run . --main-class examplertl.GenerateExampleBp
'

echo "Regenerated generated/*.sv"
