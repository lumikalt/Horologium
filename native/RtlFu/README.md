# RtlFu — RTL functional-unit substitution

Swaps one pipeline functional unit for cycle-accurate RTL via Verilator: the surrounding
Horologium pipeline drives the real hardware model instead of the C# functional/latency
model for that unit. `RtlBackedExecutor` (in `src/Core/Mechanism/RtlFu/`) substitutes the
RTL result for the register write and reports the model's observed cycle count as the
instruction's FU latency (`ExecuteResult.LatencyOverride`), which the `ooo` and `cpr`
pipelines use in place of the static `FuLatencyConfig` entry.

## Files

| File | Role |
|---|---|
| `DivUnit.scala` | Chisel source: RV32M DIV/DIVU/REM/REMU, sequential restoring divider with early termination (latency = significant-bits(dividend) + 1; RISC-V special cases resolve in 1 cycle) |
| `generated/DivUnit.sv` | Committed firtool output — consumers never need a JVM |
| `generate.sh` | Chisel → SystemVerilog (`nix-shell -p scala-cli circt`); rerun after editing the Chisel |
| `rtl_fu_shim.cpp` | Verilator harness exposing the C ABI below; model-agnostic via `-DRTL_FU_MODEL` |
| `build.sh <sv> <top> <out.so>` | Verilates + links the shared library (`nix-shell -p verilator python3` fallback) |

## C ABI

```c
void* rtl_create();                 // construct + reset the verilated model
void  rtl_destroy(void*);
int   rtl_execute(void*, unsigned op, unsigned a, unsigned b,
                  unsigned* result, int* cycles);  // 0 = ok, -1 = model hung
```

`cycles` counts clock edges from request acceptance until `io_resp_valid` — the FU
latency the pipeline charges for the instruction.

## Port contract

A wrapped module must expose (a Chisel module with `io.req = Flipped(Decoupled(...))`
and `io.resp = Valid(UInt)` produces exactly this):

```
clock, reset
io_req_ready, io_req_valid, io_req_bits_op, io_req_bits_a, io_req_bits_b
io_resp_valid, io_resp_bits
```

## Usage

```bash
native/RtlFu/build.sh native/RtlFu/generated/DivUnit.sv DivUnit /tmp/rtl_div.so
dotnet run --project src/Apps/Runner -- prog.elf --rtl-div-lib /tmp/rtl_div.so
```

Thread-safety: one verilated model per `RtlFfiFunctionalUnit`, one unit per
pipeline/thread — parallel config sweeps construct one mechanism (and thus one model)
per worker thread.
