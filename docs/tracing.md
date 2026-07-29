# Trace Formats

Four ways to get a dynamic instruction/dependency trace out of (or into) Horologium.

## Instruction trace output (Olympia, RiscV32/Trace)

`Experiment.WriteOlympiaTrace(workload, mechanism, output)` runs the workload functionally on the single-cycle train (
via `OlympiaJsonTraceWriter`, an `ICommitObserver`) and emits
an [Olympia](https://github.com/riscv-software-src/riscv-perf-model)-compatible JSON instruction trace — one object per
retired instruction with `mnemonic`, `rs1`/`rs2`/`rd`, `csr`, and `vaddr` (loads/stores). The CLI exposes it as
`--trace-json <path>`. It reuses `RvDisassembler` for the mnemonic, `ITooth` for registers, and a `TracingMemory`
wrapper for the effective address. Olympia is trace-driven (it replays the stream through its timing model without
functional execution), so this is the prerequisite for *timing* co-simulation against the Sparta-based RISC-V
performance model the project is patterned on — distinct from the *functional* Spike co-sim, which verifies ISA
correctness.

Olympia (and its Sparta framework) are packaged by the flake from source — `nix/{softfloat,sparta,olympia}.nix`, exposed
as `packages.{softfloat,sparta,olympia}` and on PATH inside `nix develop`. End to end:

```bash
dotnet run --project src/Apps/Runner -- TestBinaries/rich.elf --trace-json trace.json
nix run .#olympia -- trace.json --report-all report.txt   # IPC / cycles / retired in report.txt
```

## Elastic DDG trace (HELF format, RiscV32/Trace)

`Experiment.WriteElasticTrace(workload, mechanism, output)` records a **dynamic dependence graph** (DDG) trace in the
Horologium-native `HELF` binary format. Each committed instruction becomes one record containing its sequence number,
PC, raw encoding, instruction type (COMP/LOAD/STORE), estimated computation latency (`compDelay` from
`FuLatencyConfig.Default`), effective address and access size (loads/stores), register RAW producer seqnos (`robDeps`),
and memory RAW producer seqnos (`addrDeps`).

Register dependences track the unified 0–63 namespace (0–31 = integer, 32–63 = FP) and vector registers v0–v31
separately. Memory dependences map effective address → most-recent store seqno. `TracingMemory` supplies the effective
address; the vAddr gate is class-based (not `HasAccess`) to avoid conflating instruction-fetch addresses with data
addresses.

```bash
# Record a DDG trace
dotnet run --project src/Apps/Runner -- prog.elf --elastic-record prog.helf

# Replay the critical-path dataflow DAG and print IPC upper bound
dotnet run --project src/Apps/Runner -- --elastic-replay prog.helf
# → Elastic replay: 1234567 instructions, 890123 cycles (critical path), IPC upper bound = 1.386
```

`ElasticTraceReplayer.Replay(records)` computes the critical-path length: for each instruction,
`completionTime = max(completionTime[dep] for dep in robDeps ∪ addrDeps) + compDelay`. The result is a **dataflow IPC
upper bound** — infinite issue width, no structural hazards; real-hardware IPC will be lower.

**gem5 Protobuf translators** convert HELF to the two trace files gem5 TraceCPU requires. Fields and framing are
verified against `gem5/src/proto/inst_dep_record.proto`, `packet.proto`, and `protoio.cc`:

```bash
# Convert HELF → gem5 inst_dep_record.proto stream (dataTraceFile)
dotnet run --project src/Apps/Runner -- --elastic-to-gem5 prog.helf prog.gem5data

# Convert HELF → gem5 packet.proto fetch-trace stream (instTraceFile)
dotnet run --project src/Apps/Runner -- --fetch-to-gem5 prog.helf prog.gem5fetch

# Replay via gem5 TraceCPU (gem5 must be on PATH; see nix build .#gem5)
gem5 gem5-scripts/trace_cpu_riscv.py \
    --data-trace-file prog.gem5data \
    --inst-trace-file prog.gem5fetch
```

`Gem5ElasticTraceConverter` produces the `dataTraceFile`: LE magic `0x356d6567` + varint32-length-prefixed
`InstDepRecordHeader` then one `InstDepRecord` per committed instruction. `Gem5FetchTraceConverter` produces the
`instTraceFile`: same framing, `PacketHeader` + one `Packet` per instruction (cmd=ReadReq, addr=PC, size=4,
flags=INST\_FETCH). The fetch trace is an approximation: one 4-byte read per committed instruction, not the wrong-path
cache-line fetches a real O3 CPU would generate.

## STF binary trace (Sparcians stf\_lib format, RiscV32/Trace)

`Experiment.WriteStfTrace(workload, mechanism, output)` records a **Simulation Trace Format** binary trace. STF is the
native trace format of [Olympia](https://github.com/riscv-software-src/riscv-perf-model) and
the [Sparcians stf\_lib](https://github.com/sparcians/stf_lib). Unlike the JSON Olympia trace, the STF output includes *
*operand values** for integer and floating-point registers (feature flag `STF_CONTAIN_OPERAND_VALUE`), memory access
addresses and data, and taken-branch targets.

```bash
# Record an STF trace
dotnet run --project src/Apps/Runner -- prog.elf --stf-record prog.stf

# Replay through Olympia (stf_lib-based tools: stf_dump, stf_check, etc.)
# nix run .#olympia -- --input-file prog.stf ...
```

Format details: version 1.5, ISA=RISCV, IEM=RV32. Per-instruction record group (in ascending descriptor order):
`STF_INST_PC_TARGET` (taken branches only) → `STF_INST_REG` source records → `STF_INST_REG` dest record →
`STF_INST_MEM_ACCESS` + `STF_INST_MEM_CONTENT` (loads/stores/atomics) → `STF_INST_OPCODE32/16` (instruction boundary).
FP registers are tracked via the unified integer+FP register file (indices 32–63 = f0–f31). Vector register records are
omitted since VRF values are not accessible through `IArchState`. Generator: `STF_GEN_RESERVED` (0).

## ChampSim trace replay (RiscV32/Trace)

`ChampSimTraceReader`/`ChampSimTraceReplayer` replay a [ChampSim](https://github.com/ChampSim/ChampSim) binary trace —
the baseline format for the CBP (branch prediction) and CRC (cache replacement) competitions — through Horologium's
`IBranchPredictor` and `IReplacementPolicy` surfaces, so competition submissions can be cross-checked against real
trace corpuses rather than only Horologium-generated workloads. This is a standalone replay: it needs no ELF workload,
since a ChampSim trace already carries the full dynamic instruction/branch/memory stream.

```bash
# Evaluate a built-in predictor and cache policy against a trace (raw or gzip)
dotnet run --project src/Apps/Runner -- --champsim-trace bzip2.trace.gz \
    --champsim-predictor tage_sc_l --champsim-cache-policy Ship

# Evaluate a native CBP-3/5 plugin instead (see native/CbpShim)
dotnet run --project src/Apps/Runner -- --champsim-trace bzip2.trace.gz --champsim-cbp-lib ./libpredictor.so

# Skip cache evaluation
dotnet run --project src/Apps/Runner -- --champsim-trace bzip2.trace.gz --champsim-cache-policy none
```

Format details: a dense stream of 64-byte `input_instr` records (`inc/trace_instruction.h`) — `ip` (u64), `is_branch`/
`branch_taken` (u8 each), 2 destination + 4 source register indices (u8 each), then 2 destination + 4 source memory
addresses (u64 each, 0 = unused slot). Little-endian, no header; `.gz`-compressed streams are decompressed
transparently, `.xz` must be decompressed externally first. ChampSim traces carry no static decode information, so a
branch's actual target is taken to be the next record's `ip` — the same inference ChampSim's own `tracereader.h` uses
— meaning the final branch in a trace is unscored. Cache replay runs against a synthetic `ChampSimBackingMemory` (data
is irrelevant to hit/miss accounting); loads come from `source_memory` slots, stores from `destination_memory` slots.

