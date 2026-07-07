# Runner Execution Examples

## Basic Simulation

```bash
# Built-in countdown loop (no ELF needed — sanity check)
dotnet run --project Runner

# Run qsort with the default sweep (five_stage + OoO 2-wide/4-wide, branch predictor variants)
dotnet run --project Runner -- TestBinaries/benchmarks/qsort.elf

# Run multiple workloads in parallel and get a Markdown comparison table
dotnet run --project Runner -- \
  TestBinaries/benchmarks/median.elf \
  TestBinaries/benchmarks/qsort.elf  \
  TestBinaries/benchmarks/memcpy.elf
```

## Hardware Configuration Sweep

The sweep file is a JSON array of named configs. The `--format` flag controls output.

```bash
# Branch predictor comparison across five strategies, five-stage pipeline
cat > sweep.json << 'EOF'
[
  {"name": "always_nt",  "config": {"predictor": {"type": "always_not_taken"}}},
  {"name": "2bit",       "config": {"predictor": {"type": "n_bit", "bits": 2}}},
  {"name": "gshare",     "config": {"predictor": {"type": "gshare", "history_length": 12}}},
  {"name": "tage",       "config": {"predictor": {"type": "ltage"}}},
  {"name": "oracle",     "config": {"predictor": {"type": "true_oracle"}}}
]
EOF
dotnet run --project Runner -- TestBinaries/benchmarks/qsort.elf --sweep sweep.json

# OoO at various widths with and without cache
cat > ooo.json << 'EOF'
[
  {"name": "ooo_2w_nc",  "config": {"pipeline": "ooo", "issue_width": 2}},
  {"name": "ooo_4w_nc",  "config": {"pipeline": "ooo", "issue_width": 4, "rob_capacity": 64}},
  {"name": "ooo_2w_L1",  "config": {"pipeline": "ooo", "issue_width": 2,
    "d_cache": {"capacity_bytes": 16384, "ways": 4, "block_bytes": 64, "miss_latency": 10}}},
  {"name": "ooo_4w_L1",  "config": {"pipeline": "ooo", "issue_width": 4, "rob_capacity": 64,
    "d_cache": {"capacity_bytes": 16384, "ways": 4, "block_bytes": 64, "miss_latency": 10}}}
]
EOF
dotnet run --project Runner -- TestBinaries/benchmarks/multiply.elf --sweep ooo.json

# CSV output for graphing
dotnet run --project Runner -- TestBinaries/benchmarks/qsort.elf \
  --sweep ooo.json --format csv

# Time-series snapshot every 10 000 ticks (tracks IPC/cache-hit-rate evolution during run)
dotnet run --project Runner -- TestBinaries/benchmarks/rsort.elf \
  --sweep ooo.json --snapshot-interval 10000 --format ts-csv
```

## Spike Lock-Step Co-Simulation

Spike co-sim runs through the test suite, not the Runner. From the repo root:

```bash
# Run the three co-sim fixtures (test.elf, rich.elf, htif.elf) across
# SingleCycleTrain, FiveStageTrain, and OooeTrain
dotnet test Tests/ --filter "FullyQualifiedName~SpikeCoSim"

# CI mode — fail hard if spike/dtc are absent rather than skip
HOROLOGIUM_REQUIRE_COSIM=1 dotnet test Tests/ --filter "FullyQualifiedName~SpikeCoSim"

# Full riscv-tests conformance co-sim (71 rv32ui/m/a/c/f ELFs, commit-for-commit)
dotnet test Tests/ --filter "FullyQualifiedName~SingleCycle_Conformance_MatchesSpike"
```

## Timing Co-Simulation with Olympia (JSON Trace Path)

```bash
# Step 1: emit an Olympia-compatible JSON instruction trace
dotnet run --project Runner -- TestBinaries/benchmarks/qsort.elf \
  --trace-json qsort_trace.json

# Step 2: run through Olympia at three architectural widths
nix run .#olympia -- qsort_trace.json --arch small_core  --report-all report_small.txt
nix run .#olympia -- qsort_trace.json --arch medium_core --report-all report_medium.txt
nix run .#olympia -- qsort_trace.json --arch big_core    --report-all report_big.txt

# Pull the IPC lines
grep 'ipc =' report_small.txt report_medium.txt report_big.txt
```

The calibration script runs this end-to-end for all benchmark workloads and prints a
side-by-side IPC table (Horologium no-cache / L1 / L1+write-buffer / matched vs.
Olympia small/medium/big):

```bash
bash scripts/olympia-calibrate.sh
```

## STF Binary Trace (Olympia Native Format, Includes Register Values)

```bash
# Record — richer than JSON: operand values, memory addresses, taken-branch targets
dotnet run --project Runner -- TestBinaries/rich.elf --stf-record rich.stf

# Inspect with stf_dump (stf_lib tool, available in nix develop)
stf_dump rich.stf | head -50

# Replay through Olympia directly
nix run .#olympia -- --input-file rich.stf --report-all report.txt
```

## Elastic DDG Trace (Dataflow IPC Upper Bound)

```bash
# Record a dynamic dependence graph trace in HELF binary format
dotnet run --project Runner -- TestBinaries/benchmarks/qsort.elf \
  --elastic-record qsort.helf

# Replay — prints critical-path cycle count and IPC upper bound
# (infinite issue width, no structural hazards — true dataflow limit)
dotnet run --project Runner -- --elastic-replay qsort.helf

# Convert to gem5 TraceCPU format
dotnet run --project Runner -- --elastic-to-gem5 qsort.helf qsort.gem5data
dotnet run --project Runner -- --fetch-to-gem5   qsort.helf qsort.gem5fetch

gem5 gem5-scripts/trace_cpu_riscv.py \
  --data-trace-file qsort.gem5data \
  --inst-trace-file qsort.gem5fetch
```

## Custom Machine Spec via Scripting Host

```bash
# Run with the example C# spec (five-stage, L1, TLB)
dotnet run --project Runner -- TestBinaries/benchmarks/qsort.elf \
  --script scripts/example.csx

# Same spec written in F#
dotnet run --project Runner -- TestBinaries/benchmarks/qsort.elf \
  --script scripts/example.fsx

# Inline OoO spec in a throwaway script
cat > /tmp/ooo.csx << 'EOF'
new MachineSpec(
    new OooSpec(IssueWidth: 4, RobCapacity: 64, ExtraPhysRegs: 48),
    () => new Rv32Mechanism(),
    CacheHierarchySpec.SplitId(
        new CachePathSpec([new CacheLevelSpec(16384, 4, 64, 10)], new TlbSpec(64)),  // I$
        new CachePathSpec([new CacheLevelSpec(16384, 4, 64, 10)], new TlbSpec(64))   // D$
    )
)
EOF
dotnet run --project Runner -- TestBinaries/benchmarks/qsort.elf --script /tmp/ooo.csx
```

## Region-of-Interest + Checkpointing

```bash
# Fast-forward to roi_begin (single-cycle), then time the ROI with the script's pipeline
dotnet run --project Runner -- TestBinaries/benchmarks/qsort.elf \
  --script scripts/example.csx \
  --roi-start roi_begin --roi-end roi_end \
  --max-ticks 5000000

# Two-phase handoff: fast-forward 100 M instructions on single-cycle, save state,
# resume on a detailed OoO model
dotnet run --project Runner -- TestBinaries/benchmarks/qsort.elf \
  --script scripts/example.csx \
  --max-ticks 100000000 \
  --checkpoint-save fast.chk

dotnet run --project Runner -- TestBinaries/benchmarks/qsort.elf \
  --script /tmp/ooo.csx \
  --checkpoint-load fast.chk \
  --max-ticks 10000000
```
