#!/usr/bin/env bash
# gem5 O3CPU vs Horologium OooeTrain IPC comparison.
#
# Runs each riscv-tests benchmark through:
#   (a) gem5 RiscvO3CPU SE mode   — Linux-ABI ELF from gem5-bmarks/
#   (b) Horologium OooeTrain      — HTIF ELF from TestBinaries/benchmarks/
#
# Both simulators use parameters matching Horologium's +Matched calibration
# profile (w2): ROB=30, IQ=8 per class, L1 16KB, LoadHitLatency=4,
# BypassLatency=1, DivLatency=23.  See docs/olympia-calibration.md.
#
# Usage:
#   bash scripts/gem5-compare.sh [--width N] [--rob N] [--div-lat N] [--mem-lat-ns STR]
#
# --div-lat N       IntDiv latency passed to both gem5 and Horologium (default: 23).
#                   gem5 default FUPool uses 20; Horologium default is 23.
# --mem-lat-ns STR  SimpleMemory latency for gem5 (default: "30ns").
#                   Pass "10ns" to eliminate DRAM asymmetry with Horologium HtifMemory.
#
# Prerequisites:
#   gem5 on PATH  (nix build .#gem5 or nix develop)
#   dotnet build  (run once from repo root)
#   gem5-bmarks/*.elf  (make -C gem5-bmarks benchmarks)

set -euo pipefail
cd "$(dirname "$0")/.."

WIDTH=2; ROB=30; IQ=8; DIV_LAT=23; MEM_LAT_NS="30ns"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --width)      WIDTH=$2;      shift 2 ;;
        --rob)        ROB=$2;        shift 2 ;;
        --iq)         IQ=$2;         shift 2 ;;
        --div-lat)    DIV_LAT=$2;    shift 2 ;;
        --mem-lat-ns) MEM_LAT_NS=$2; shift 2 ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

BMARKS="median qsort rsort towers vvadd memcpy multiply gcd treesum pchase"

# ── Horologium +Matched sweep config ─────────────────────────────────────────
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
cat > "$TMP/sweep.json" <<JSON
[{"name":"w${WIDTH}","config":{
  "pipeline":"ooo","issue_width":${WIDTH},"rob_capacity":${ROB},"iq_capacity":${IQ},
  "predictor":{"type":"l_tage"},
  "i_cache":{"capacity_bytes":16384,"ways":4,"block_bytes":64,"miss_latency":10},
  "d_cache":{"capacity_bytes":16384,"ways":4,"block_bytes":64,"miss_latency":10},
  "store_buffer_capacity":2,
  "fu_latency":{"load_hit_latency":4,"bypass_latency":1,"div_latency":${DIV_LAT}}
}}]
JSON

dotnet build -c Release --no-restore -v quiet 2>/dev/null || true
mkdir -p m5out-compare

printf "\nHorologium vs gem5 O3CPU — w%d  ROB=%d  IQ=%d  L1 16KB  LoadHit=4  Bypass=1  DivLat=%d  MemLat=%s\n\n" \
    "$WIDTH" "$ROB" "$IQ" "$DIV_LAT" "$MEM_LAT_NS"
printf "%-10s  %8s  %9s  %9s  %8s\n" benchmark "gem5 IPC" "Horo IPC" "H/G ratio" "Δinsts"
printf "%-10s  %8s  %9s  %9s  %8s\n" ---------- -------- --------- --------- --------

for B in $BMARKS; do
    # ── gem5 run ──────────────────────────────────────────────────────────────
    GEM5_DIR="m5out-compare/$B"; mkdir -p "$GEM5_DIR"
    gem5 --quiet --outdir="$GEM5_DIR" \
        gem5-scripts/o3cpu_riscv.py --cmd "gem5-bmarks/$B.elf" \
        --width "$WIDTH" --rob "$ROB" --iq "$IQ" \
        --div-lat "$DIV_LAT" --mem-lat-ns "$MEM_LAT_NS" \
        >/dev/null 2>&1 || true

    GEM5_IPC=$(  grep "^system\.cpu\.ipc\b"                                      "$GEM5_DIR/stats.txt" 2>/dev/null | awk '{printf "%.4f",$2}')
    GEM5_INSTS=$(grep "^system\.cpu\.commit\.committedInstType_0::total\b"        "$GEM5_DIR/stats.txt" 2>/dev/null | awk '{print $2}')
    [[ -z "$GEM5_IPC"   ]] && GEM5_IPC="err"
    [[ -z "$GEM5_INSTS" ]] && GEM5_INSTS=""

    # ── Horologium run (single dotnet call) ───────────────────────────────────
    HORO_ROW=$(dotnet run --project Runner --no-build -- \
        "TestBinaries/benchmarks/$B.elf" --sweep "$TMP/sweep.json" \
        --max-ticks 5000000 2>/dev/null \
        | grep "| w${WIDTH} |")

    # pipeline.ipc is the last numeric column; pipeline.retired is the 10th pipe-field
    HORO_IPC=$(  echo "$HORO_ROW" | awk -F'|' '{gsub(/ /,"",$(NF-1)); print $(NF-1)}')
    HORO_INSTS=$(echo "$HORO_ROW" | awk -F'|' '{gsub(/ /,"",$12); print $12}')
    [[ -z "$HORO_IPC"   ]] && HORO_IPC="err"
    [[ -z "$HORO_INSTS" ]] && HORO_INSTS=""

    # ── Compute ratio and Δinsts ──────────────────────────────────────────────
    RATIO="n/a"
    if [[ "$GEM5_IPC" != "err" && "$HORO_IPC" != "err" ]]; then
        RATIO=$(awk "BEGIN{printf \"%.3f\", $HORO_IPC / $GEM5_IPC}")
    fi
    DELTA="n/a"
    if [[ -n "$GEM5_INSTS" && -n "$HORO_INSTS" && "$GEM5_INSTS" -gt 0 ]]; then
        DELTA=$(awk "BEGIN{printf \"%+.1f%%\", ($HORO_INSTS-$GEM5_INSTS)/$GEM5_INSTS*100}")
    fi

    printf "%-10s  %8s  %9s  %9s  %8s\n" "$B" "$GEM5_IPC" "$HORO_IPC" "$RATIO" "$DELTA"
done

printf "\nNotes:\n"
printf "  gem5 : RiscvO3CPU SE mode, TournamentBP+RAS(16), SimpleBTB(4096), L1 16KB split, %s DRAM, IntDiv=%d\n" \
    "$MEM_LAT_NS" "$DIV_LAT"
printf "  Horo : OooeTrain +Matched — per-class IQ(5×%d), RAS(16), LTage, HTIF binary (kernel-only IPC), DivLat=%d\n" \
    "$IQ" "$DIV_LAT"
printf "  ratio: Horo IPC / gem5 IPC  (>1 = Horologium faster than gem5)\n"
printf "  Δinsts: (Horo_retired − gem5_committed) / gem5_committed\n"
printf "          A small delta is expected: different startup/exit code paths.\n"
printf "          A large delta suggests the programs are executing different kernels.\n"
