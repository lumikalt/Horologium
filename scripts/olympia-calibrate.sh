#!/usr/bin/env bash
# Olympia timing calibration harness.
#
# For each integer workload: emit a Horologium instruction trace, run it through
# Olympia at three arch widths, and run Horologium's own OoO train at matching
# widths (no-cache, L1-only, L1+write-buffer, matched). Prints an IPC comparison table.
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

# OoO sweep at widths 2/3/8 (= Olympia small/medium/big_core).
# Four Horologium configurations:
#   no-cache  — pure timing baseline, load latency 1 cycle
#   L1$       — 16 KB split I/D, 10-cycle miss penalty, load-side MLP, lump-sum store-commit
#   L1$+WB    — same L1, write buffer sized to issue_width (2/3/8 slots per width)
#   +Matched  — WB∝w + 4-cycle load-hit latency + D$ sized to match Olympia (16/32/64 KB)
# (Olympia small/medium/big_core use 16/32/64 KB D$. LoadHitLatency=4 models Olympia's
# 5-stage LSU pipeline: addr_calc → MMU → cache_lookup → cache_read → complete.)
cat > "$TMP/nocache.json" <<'JSON'
[{"name":"w2","config":{"pipeline":"ooo","issue_width":2,"rob_capacity":32,"predictor":{"type":"n_bit","bits":2}}},
 {"name":"w3","config":{"pipeline":"ooo","issue_width":3,"rob_capacity":48,"predictor":{"type":"n_bit","bits":2}}},
 {"name":"w8","config":{"pipeline":"ooo","issue_width":8,"rob_capacity":128,"predictor":{"type":"n_bit","bits":2}}}]
JSON
L1='"i_cache":{"capacity_bytes":16384,"ways":4,"block_bytes":64,"miss_latency":10},"d_cache":{"capacity_bytes":16384,"ways":4,"block_bytes":64,"miss_latency":10}'
cat > "$TMP/cache.json" <<JSON
[{"name":"w2","config":{"pipeline":"ooo","issue_width":2,"rob_capacity":32,"predictor":{"type":"n_bit","bits":2},$L1}},
 {"name":"w3","config":{"pipeline":"ooo","issue_width":3,"rob_capacity":48,"predictor":{"type":"n_bit","bits":2},$L1}},
 {"name":"w8","config":{"pipeline":"ooo","issue_width":8,"rob_capacity":128,"predictor":{"type":"n_bit","bits":2},$L1}}]
JSON
cat > "$TMP/cache_wb.json" <<JSON
[{"name":"w2","config":{"pipeline":"ooo","issue_width":2,"rob_capacity":32,"predictor":{"type":"n_bit","bits":2},$L1,"store_buffer_capacity":2}},
 {"name":"w3","config":{"pipeline":"ooo","issue_width":3,"rob_capacity":48,"predictor":{"type":"n_bit","bits":2},$L1,"store_buffer_capacity":3}},
 {"name":"w8","config":{"pipeline":"ooo","issue_width":8,"rob_capacity":128,"predictor":{"type":"n_bit","bits":2},$L1,"store_buffer_capacity":8}}]
JSON
# +Matched: WB∝w + 4-cycle load-hit latency + 1-cycle bypass latency + D$ sized to Olympia's per-width defaults
# + per-width INT ALU count (w8: int_alu_count=3 to match Olympia big_core's 3 INT execution ports)
# + ROB capacity=30 to match Olympia's retire_queue_depth=30 (fixed across all arch widths in Olympia YAMLs).
# Olympia small/medium/big_core all use the same 30-slot ROB; Horologium previously used 32/48/128.
# Reducing ROB limits wrong-path speculation window, cutting misprediction flush overhead at wider widths.
FU4='"fu_latency":{"load_hit_latency":4,"bypass_latency":1,"div_latency":23}'
FU4W8='"fu_latency":{"int_alu_count":3,"load_hit_latency":4,"bypass_latency":1,"div_latency":23}'
IC='"i_cache":{"capacity_bytes":16384,"ways":4,"block_bytes":64,"miss_latency":10}'
cat > "$TMP/cache_wb_matched.json" <<JSON
[{"name":"w2","config":{"pipeline":"ooo","issue_width":2,"rob_capacity":30,"predictor":{"type":"n_bit","bits":2},$IC,"d_cache":{"capacity_bytes":16384,"ways":4,"block_bytes":64,"miss_latency":10},"store_buffer_capacity":2,$FU4}},
 {"name":"w3","config":{"pipeline":"ooo","issue_width":3,"rob_capacity":30,"predictor":{"type":"n_bit","bits":2},$IC,"d_cache":{"capacity_bytes":32768,"ways":8,"block_bytes":64,"miss_latency":10},"store_buffer_capacity":3,$FU4}},
 {"name":"w8","config":{"pipeline":"ooo","issue_width":8,"rob_capacity":30,"predictor":{"type":"n_bit","bits":2},$IC,"d_cache":{"capacity_bytes":65536,"ways":8,"block_bytes":64,"miss_latency":10},"store_buffer_capacity":8,$FU4W8}}]
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
# gcd: div/rem-heavy (Euclidean algorithm); stresses MulDivLatency mismatch.
# treesum: two-call recursion (tree_sum calls itself twice) — RAS stress test.
# pchase: pointer-chase through 64 KB shuffled array; serial load-dependency chain.
WORKLOADS=("rich:TestBinaries/rich.elf")
for b in vvadd multiply median towers qsort rsort memcpy gcd treesum pchase; do
  WORKLOADS+=("$b:TestBinaries/benchmarks/$b.elf")
done

printf '%-9s | %-20s | %-20s | %-20s | %-20s | %-20s\n' \
  workload 'Horologium (w2/3/8)' '+ L1$ (w2/3/8)' '+ WB∝w (w2/3/8)' '+Matched (w2/3/8)' 'Olympia (s/m/b)'
printf -- '----------+----------------------+----------------------+----------------------+----------------------+---------------------\n'
for spec in "${WORKLOADS[@]}"; do
  name=${spec%%:*}; elf=${spec##*:}
  dotnet run --project Runner -- "$elf" --trace-json "$TMP/t.json" >/dev/null 2>&1
  printf '%-9s | %5s %5s %5s    | %5s %5s %5s    | %5s %5s %5s    | %5s %5s %5s    | %5s %5s %5s\n' "$name" \
    "$(horo "$elf" "$TMP/nocache.json" w2)" "$(horo "$elf" "$TMP/nocache.json" w3)" "$(horo "$elf" "$TMP/nocache.json" w8)" \
    "$(horo "$elf" "$TMP/cache.json"   w2)" "$(horo "$elf" "$TMP/cache.json"   w3)" "$(horo "$elf" "$TMP/cache.json"   w8)" \
    "$(horo "$elf" "$TMP/cache_wb.json"         w2)" "$(horo "$elf" "$TMP/cache_wb.json"         w3)" "$(horo "$elf" "$TMP/cache_wb.json"         w8)" \
    "$(horo "$elf" "$TMP/cache_wb_matched.json" w2)" "$(horo "$elf" "$TMP/cache_wb_matched.json" w3)" "$(horo "$elf" "$TMP/cache_wb_matched.json" w8)" \
    "$(oly "$TMP/t.json" small_core)" "$(oly "$TMP/t.json" medium_core)" "$(oly "$TMP/t.json" big_core)"
done
