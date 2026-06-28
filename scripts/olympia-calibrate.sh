#!/usr/bin/env bash
# Olympia timing calibration harness (TODO "Phase 2b").
#
# For each integer workload: emit a Horologium instruction trace, run it through
# Olympia at three arch widths, and run Horologium's own OoO train at matching
# widths (with and without an L1 cache). Prints an IPC comparison table.
#
# This is a *descriptive* cross-model comparison, NOT a fit — Olympia is not
# ground truth. See docs/olympia-calibration.md for interpretation and caveats.
#
# Requires `olympia` on PATH (nix build .#olympia / nix develop) and the .NET
# Runner. Only integer workloads work today: the JSON trace writer is
# integer-focused, so FP register operands are rejected by Mavis (every
# riscv-tests benchmark is FP — hence only test.elf / rich.elf here).
set -euo pipefail
cd "$(dirname "$0")/.."

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

# OoO sweep, no cache vs L1 I/D cache; widths 2/3/8 to match Olympia small/med/big.
# Widths 2/3/8 match Olympia small/medium/big_core. NOTE: a cache-matched
# comparison is not possible yet — adding an L1 to the OoO train inflates cycles
# pathologically (median: 16k -> 1.0M cycles, ~73 dcache "hits" per instruction),
# a Horologium OoO+cache bug this study surfaced (see TODO/docs). So this is the
# *unmatched* comparison: Horologium has 1-cycle loads, Olympia a real hierarchy.
cat > "$TMP/nocache.json" <<'JSON'
[{"name":"w2","config":{"pipeline":"ooo","issue_width":2,"rob_capacity":32,"predictor":{"type":"n_bit","bits":2}}},
 {"name":"w3","config":{"pipeline":"ooo","issue_width":3,"rob_capacity":48,"predictor":{"type":"n_bit","bits":2}}},
 {"name":"w8","config":{"pipeline":"ooo","issue_width":8,"rob_capacity":128,"predictor":{"type":"n_bit","bits":2}}}]
JSON

horo() { # elf sweep.json name
  dotnet run --project Runner -- "$1" --sweep "$2" 2>/dev/null \
    | grep "| $3 |" | awk -F'|' '{gsub(/ /,"",$(NF-1)); print $(NF-1)}'
}
oly() { # trace arch
  olympia "$1" --arch "$2" --report-all "$TMP/r.txt" >/dev/null 2>&1 || { echo "ERR"; return; }
  grep -E '^\s+ipc =' "$TMP/r.txt" | head -1 | grep -oE '[0-9.]+'
}

# Workloads: rich.elf plus the riscv-tests benchmark suite. The opcode-based
# trace writer ingests FP, so the benchmarks (all FP) now work.
WORKLOADS=("rich:TestBinaries/rich.elf")
for b in vvadd multiply median towers qsort rsort memcpy; do
  WORKLOADS+=("$b:TestBinaries/benchmarks/$b.elf")
done

printf '%-9s | %-22s | %-22s\n' workload 'Horologium OoO (w2/3/8)' 'Olympia (small/med/big)'
printf -- '----------+------------------------+------------------------\n'
for spec in "${WORKLOADS[@]}"; do
  name=${spec%%:*}; elf=${spec##*:}
  dotnet run --project Runner -- "$elf" --trace-json "$TMP/t.json" >/dev/null 2>&1
  printf '%-9s | %5s %5s %5s        | %5s %5s %5s\n' "$name" \
    "$(horo "$elf" "$TMP/nocache.json" w2)" "$(horo "$elf" "$TMP/nocache.json" w3)" "$(horo "$elf" "$TMP/nocache.json" w8)" \
    "$(oly "$TMP/t.json" small_core)" "$(oly "$TMP/t.json" medium_core)" "$(oly "$TMP/t.json" big_core)"
done
