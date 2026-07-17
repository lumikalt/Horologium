# RtlFu — RTL unit substitution

Swaps one pipeline unit for cycle-accurate RTL via Verilator: the surrounding Horologium
pipeline drives the real hardware model instead of the C# model for that unit. Two unit
kinds so far:

- **Functional units** — `RtlBackedExecutor` (in `src/Core/Mechanism/RtlFu/`) substitutes
  the RTL result for the register write and reports the model's observed cycle count as
  the instruction's FU latency (`ExecuteResult.LatencyOverride`), which the `ooo` and
  `cpr` pipelines use in place of the static `FuLatencyConfig` entry.
- **Branch predictors** — `RtlFfiBranchPredictor` implements `IBranchPredictor` on top of
  a verilated predictor: combinational predict at fetch, one-clock-edge update at commit.
  Selectable per sweep config (`{"type": "rtl_bp_plugin"}`) or globally via `--rtl-bp-lib`.

## Files

| File | Role |
|---|---|
| `DivUnit.scala` | Chisel source: RV32M DIV/DIVU/REM/REMU, sequential restoring divider with early termination (latency = significant-bits(dividend) + 1; RISC-V special cases resolve in 1 cycle) |
| `GshareBp.scala` | Chisel source: gshare predictor (8-bit GHR, 256×2-bit PHT + BTB) mirroring the C# `GsharePredictor` bit-for-bit so differential tests can demand identical predictions |
| `generated/*.sv` | Committed firtool output — consumers never need a JVM |
| `generate.sh` | Chisel → SystemVerilog (`nix-shell -p scala-cli circt`); rerun after editing the Chisel |
| `rtl_fu_shim.cpp` | Verilator harness for functional units; model-agnostic via `-DRTL_MODEL` |
| `rtl_bp_shim.cpp` | Verilator harness for branch predictors (`rtl_bp_predict`/`rtl_bp_update`) |
| `build.sh <sv> <top> <out.so> [shim]` | Verilates + links the shared library (`nix-shell -p verilator python3` fallback); shim defaults to `rtl_fu_shim.cpp` |

## Functional-unit C ABI

```c
void* rtl_create();                 // construct + reset the verilated model
void  rtl_destroy(void*);
int   rtl_execute(void*, unsigned op, unsigned a, unsigned b,
                  unsigned* result, int* cycles);  // 0 = ok, -1 = model hung
```

`cycles` counts clock edges from request acceptance until `io_resp_valid` — the FU
latency the pipeline charges for the instruction.

## Branch-predictor C ABI

```c
void* rtl_bp_create();
void  rtl_bp_destroy(void*);
void  rtl_bp_predict(void*, unsigned long long pc, int* taken, unsigned long long* target);
void  rtl_bp_update(void*, unsigned long long pc, int taken, unsigned long long target);
```

`rtl_bp_predict` is a pure combinational read (no clock edge) — a fetch-stage table
lookup; `rtl_bp_update` drives the update ports for one clock edge — the commit-time
training write.

## Port contracts

A functional unit must expose (a Chisel module with `io.req = Flipped(Decoupled(...))`
and `io.resp = Valid(UInt)` produces exactly this):

```
clock, reset
io_req_ready, io_req_valid, io_req_bits_op, io_req_bits_a, io_req_bits_b
io_resp_valid, io_resp_bits
```

A branch predictor must expose:

```
clock, reset
io_predPc, io_predTaken, io_predTarget            (combinational lookup)
io_updValid, io_updPc, io_updTaken, io_updTarget  (applied on one clock edge)
```

## Usage

```bash
native/RtlFu/build.sh native/RtlFu/generated/DivUnit.sv DivUnit /tmp/rtl_div.so
native/RtlFu/build.sh native/RtlFu/generated/GshareBp.sv GshareBp /tmp/rtl_gshare.so rtl_bp_shim.cpp
dotnet run --project src/Apps/Runner -- prog.elf --rtl-div-lib /tmp/rtl_div.so --rtl-bp-lib /tmp/rtl_gshare.so
```

Thread-safety: one verilated model per `RtlFfiFunctionalUnit`, one unit per
pipeline/thread — parallel config sweeps construct one mechanism (and thus one model)
per worker thread.
