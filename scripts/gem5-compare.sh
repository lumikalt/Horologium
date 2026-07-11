#!/usr/bin/env bash
# gem5 O3CPU vs Horologium OooeTrain IPC comparison.
#
# Runs each riscv-tests benchmark through:
#   (a) gem5 RiscvO3CPU SE mode   — Linux-ABI ELF from gem5-bmarks/
#   (b) Horologium OooeTrain      — HTIF ELF from TestBinaries/benchmarks/
#
# Both simulators use parameters matching Horologium's +Matched calibration
# profile (w2): ROB=30, IQ=8 per class (5×8=40 total), L1 16KB,
# LoadHitLatency=4, DivLatency=23.  See docs/olympia-calibration.md.
#
# Usage:
#   bash scripts/gem5-compare.sh [--width N] [--rob N] [--iq N]
#                                 [--div-lat N] [--bypass-lat N] [--mem-lat-ns STR]
#                                 [--flat-iq] [--structurally-matched]
#                                 [--predictor-json JSON]
#
# --div-lat N           IntDiv latency for both gem5 and Horologium (default: 23).
#                       gem5 DefaultFUPool uses 20; Horologium default is 23.
# --bypass-lat N        Horologium result-forwarding latency in cycles (default: 1).
#                       gem5 O3CPU uses 0-cycle forwarding by default; pass 0 to match.
# --mem-lat-ns STR      SimpleMemory latency for gem5 (default: "30ns").
#                       Pass "10ns" to eliminate DRAM asymmetry with Horologium HtifMemory.
# --flat-iq             Use Horologium's flat (unified) IQ mode: one 40-entry IQ for all
#                       instruction classes, matching gem5's scheduling model.
# --structurally-matched  Preset: bypass-lat=0 + mem-lat-ns=10ns — the closest structural
#                       match to gem5 O3CPU (0-cycle forwarding, DRAM latency equalised).
# --predictor-json JSON Horologium predictor config as a JSON object (default: {"type":"l_tage"}).
#                       gem5-matched TournamentBP:
#                         '{"type":"tournament","LocalHistoryBits":11,"LocalTableSize":2048,"GlobalHistoryBits":13}'
#                       (gem5 TournamentBP defaults: localPredictorSize=2048,
#                        localHistoryTableSize=2048, globalPredictorSize=8192,
#                        choicePredictorSize=8192)
#
# Prerequisites:
#   gem5 on PATH  (nix build .#gem5 or nix develop)
#   dotnet build  (run once from repo root)
#   gem5-bmarks/*.elf  (make -C gem5-bmarks benchmarks)

set -euo pipefail
cd "$(dirname "$0")/.."

WIDTH=2; ROB=30; IQ=8; DIV_LAT=23; BYPASS_LAT=1; MEM_LAT_NS="30ns"; FLAT_IQ=false
PREDICTOR_JSON='{"type":"l_tage"}'
while [[ $# -gt 0 ]]; do
    case "$1" in
        --width)                WIDTH=$2;                    shift 2 ;;
        --rob)                  ROB=$2;                      shift 2 ;;
        --iq)                   IQ=$2;                       shift 2 ;;
        --div-lat)              DIV_LAT=$2;                  shift 2 ;;
        --bypass-lat)           BYPASS_LAT=$2;               shift 2 ;;
        --mem-lat-ns)           MEM_LAT_NS=$2;               shift 2 ;;
        --flat-iq)              FLAT_IQ=true;                shift ;;
        --structurally-matched) BYPASS_LAT=0; MEM_LAT_NS="10ns"; shift ;;
        --predictor-json)       PREDICTOR_JSON=$2;           shift 2 ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

# gem5 IQ: 5× the per-class Horologium IQ to match total slot count (5 classes × IQ).
GEM5_IQ=$((IQ * 5))

BMARKS="median qsort rsort towers vvadd memcpy multiply gcd treesum pchase"

# ── Horologium +Matched sweep config ─────────────────────────────────────────
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
cat > "$TMP/sweep.json" <<JSON
[{"name":"w${WIDTH}","config":{
  "pipeline":"ooo","issue_width":${WIDTH},"rob_capacity":${ROB},"iq_capacity":${IQ},
  "flat_iq":${FLAT_IQ},
  "predictor":${PREDICTOR_JSON},
  "i_cache":{"capacity_bytes":16384,"ways":4,"block_bytes":64,"miss_latency":10},
  "d_cache":{"capacity_bytes":16384,"ways":4,"block_bytes":64,"miss_latency":10},
  "store_buffer_capacity":2,
  "fu_latency":{"load_hit_latency":4,"bypass_latency":${BYPASS_LAT},"div_latency":${DIV_LAT}}
}}]
JSON

dotnet build -c Release --no-restore -v quiet 2>/dev/null || true
mkdir -p m5out-compare

IQ_MODE="per-class"
[[ "$FLAT_IQ" == "true" ]] && IQ_MODE="flat"
printf "\nHorologium vs gem5 O3CPU — w%d  ROB=%d  Horo-IQ=%s(%dx%d=%d)  gem5-IQ=%d  L1 16KB  LoadHit=4  Bypass=%d  DivLat=%d  MemLat=%s\n\n" \
    "$WIDTH" "$ROB" "$IQ_MODE" 5 "$IQ" "$GEM5_IQ" "$GEM5_IQ" "$BYPASS_LAT" "$DIV_LAT" "$MEM_LAT_NS"
printf "%-10s  %8s  %9s  %9s  %8s\n" benchmark "gem5 IPC" "Horo IPC" "H/G ratio" "Δinsts"
printf "%-10s  %8s  %9s  %9s  %8s\n" ---------- -------- --------- --------- --------

for B in $BMARKS; do
    # ── gem5 run ──────────────────────────────────────────────────────────────
    GEM5_DIR="m5out-compare/$B"; mkdir -p "$GEM5_DIR"
    gem5 --quiet --outdir="$GEM5_DIR" \
        gem5-scripts/o3cpu_riscv.py --cmd "gem5-bmarks/$B.elf" \
        --width "$WIDTH" --rob "$ROB" --iq "$GEM5_IQ" \
        --div-lat "$DIV_LAT" --mem-lat-ns "$MEM_LAT_NS" \
        >/dev/null 2>&1 || true

    GEM5_IPC=$(  grep "^system\.cpu\.ipc\b"                                      "$GEM5_DIR/stats.txt" 2>/dev/null | awk '{printf "%.4f",$2}')
    GEM5_INSTS=$(grep "^system\.cpu\.commit\.committedInstType_0::total\b"        "$GEM5_DIR/stats.txt" 2>/dev/null | awk '{print $2}')
    [[ -z "$GEM5_IPC"   ]] && GEM5_IPC="err"
    [[ -z "$GEM5_INSTS" ]] && GEM5_INSTS=""

    # ── Horologium run (single dotnet call) ───────────────────────────────────
    HORO_ROW=$(dotnet run --project src/Apps/Runner --no-build -c Release -- \
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
printf "  gem5 : RiscvO3CPU SE mode, TournamentBP+RAS(16), flat IQ(%d), L1 16KB split, %s DRAM, IntDiv=%d\n" \
    "$GEM5_IQ" "$MEM_LAT_NS" "$DIV_LAT"
printf "  Horo : OooeTrain — %s IQ(%s), RAS(16), predictor=%s, HTIF binary (kernel-only IPC), Bypass=%d, DivLat=%d\n" \
    "$IQ_MODE" "$([[ "$FLAT_IQ" == "true" ]] && echo "1×$GEM5_IQ" || echo "5×$IQ")" "$PREDICTOR_JSON" "$BYPASS_LAT" "$DIV_LAT"
printf "  ratio: Horo IPC / gem5 IPC  (>1 = Horologium faster than gem5)\n"
printf "  Δinsts: (Horo_retired − gem5_committed) / gem5_committed\n"
printf "          Both measure kernel-only: gem5 setStats(1) fires m5_reset_stats(0,0) and\n"
printf "          setStats(0) fires m5_exit(0) — same ROI window as Horologium SetStatsObserver.\n"
printf "          Δinsts ≈ 0%% is expected; a nonzero delta indicates a retired-instruction divergence.\n"
