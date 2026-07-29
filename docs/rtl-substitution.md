# RTL Unit Substitution

Individual pipeline units can be swapped for cycle-accurate RTL driven through
[Verilator](https://www.veripool.org/verilator/), so the surrounding pipeline exercises a synthesizable design instead
of the C# model for that unit — useful for validating a custom design against the rest of the system before
tapeout/FPGA. Two unit kinds so far:

- **Functional units**: `RtlBackedExecutor` decorates the ISA executor, and instructions an ISA-side selector claims
  execute on the verilated model — the RTL result becomes the register write, and the model's observed cycle count
  becomes the instruction's FU latency (`ExecuteResult.LatencyOverride`, honored by the `ooo` and `cpr` pipelines in
  place of the static `FuLatencyConfig` entry). Decorators compose, so multiple selectors can each claim their
  instruction class. The first unit is a Chisel sequential restoring divider with early termination
  (`native/RtlFu/DivUnit.scala`, selector `RvRtlDiv`: DIV/DIVU/REM/REMU), so div latency is data-dependent — 1 cycle
  for the RISC-V special cases, up to 33 for a full-width dividend. The second is a fully pipelined 3-stage 33×33
  multiplier (`native/RtlFu/MulUnit.scala`, selector `RvRtlMul`: MUL/MULH/MULHSU/MULHU) under the same port contract
  with `req.ready` constantly high; its constant 3-cycle latency equals the `MulDivLatency` default, so an RTL-mul
  run is cycle-identical to the static model. `rtl_div_lib` and `rtl_mul_lib` (sweep JSON) chain to substitute the whole
  M extension. The third is an FP divide/square-root unit (`native/RtlFu/FDivSqrtUnit.scala`, `rtl_fdiv_lib`):
  iterative IEEE binary32 FDIV.S/FSQRT.S with full subnormal support, round-to-nearest-even, canonical NaNs, and
  exception flags — carried across the FFI by a flags-reporting shim variant (`rtl_fpu_shim.cpp`,
  `rtl_execute_flags`) and delivered by the ISA-side `RvRtlFpExecutor` decorator, which replicates the §11.3
  NaN-boxing check and ORs the RTL's flags into the fflags CSR through the same SideEffect path the C# model uses.
  The differential sweep demands bit-identical NaN-boxed results *and* bit-identical fflags across IEEE specials,
  subnormals, rounding edges, and random bit patterns — including the C# model's flag quirks (overflow raises OF
  without NX; UF requires an inexact nonzero subnormal). Latency is data-dependent: 1 cycle for special cases, ~30
  for the iterative paths, vs. the static `FloatDivSqrtLatency` default of 16.
- **Branch predictors**: two shim ABIs, auto-detected by `RtlBranchPredictorLoader` so one flag/config serves both.
  `RtlFfiBranchPredictor` wraps a plain predictor — combinational predict at fetch, one-clock-edge update at commit;
  speculative-history hooks stay at their interface defaults (committed history only, like `CbpFfiPredictor`); the
  first is a Chisel gshare (`native/RtlFu/GshareBp.scala`) mirroring the C# `GsharePredictor` bit-for-bit.
  `RtlFfiHistoryBranchPredictor` wraps a predictor that manages its own speculative global history in RTL, carrying
  the full `IBranchPredictor` contract across the FFI — fetch-time history folds, flush recovery, and per-branch
  checkpoints for OoO partial squashes, where the checkpoint is the model's working-history value itself (TAGE-family
  folded indices derive from it, so a single value is a complete snapshot and no checkpoint RAM is needed); the first
  is a Chisel L-TAGE (`native/RtlFu/LTageBp.scala`: bimodal base, four tagged tables with geometric 8/13/21/34-bit
  folded histories, and a loop-predictor overlay) mirroring the C# `LTagePredictor` bit-for-bit — the differential
  test replays fetch/commit/flush/partial-squash sequences demanding identical predictions and identical checkpoints,
  and an OoO nested-loop run is cycle-for-cycle identical to the C# predictor. Selectable per sweep config
  (`{"type": "rtl_bp_plugin", "library_path": ...}`) or as the evaluated predictor of a `--champsim-trace` replay
  (`--champsim-rtl-bp-lib`).
- **Cache replacement policies**: `RtlFfiReplacementPolicy` implements `IReplacementPolicy` over a verilated policy —
  combinational victim selection with the aging write-back, hit promotion, and fill insertion each consuming one clock
  edge. Geometry is fixed at Chisel elaboration and exposed through the model (`io_cfgSets`/`io_cfgWays`); the policy
  attaches only to cache levels whose sets×ways match, others keep the configured C# policy kind. The first policy is
  a Chisel SRRIP (`native/RtlFu/SrripRp.scala`, default 64 sets × 4 ways) mirroring the C# `SrripPolicy` bit-for-bit —
  the differential test demands identical victims and RRPV metadata, and a cache-level test demands identical hit/miss
  counts from `SetAssociativeCache` under either policy. The second is a Chisel DRRIP (`native/RtlFu/DrripRp.scala`):
  the SRRIP base plus Set Dueling — SDM leader sets, a 10-bit PSEL duel, and 1/32 bimodal BRRIP inserts — mirroring
  the C# `DrripPolicy`, demonstrating global cross-set state (PSEL, shared bimodal counter) through the unchanged shim
  ABI; its differential stream phases between SDM-heavy and follower-heavy set biases so the duel swings both ways.
  Selectable per sweep config (`"rtl_cache_policy_lib"`) or as the evaluated policy of a `--champsim-trace` replay
  (`--champsim-rtl-rp-lib`; strict geometry check against the `--champsim-cache-*` flags).
- **Cache prefetchers**: `RtlFfiPrefetcher` implements `IPrefetcher` over a verilated prefetcher, with two shim
  shapes sharing one C ABI. Single-target models (`rtl_pf_shim.cpp`): each demand access is presented once, the
  prefetch decision is read combinationally (post-update semantics live in the model), and the prediction-table
  update commits on one clock edge — the first is a Chisel RPT stride prefetcher (`native/RtlFu/StridePf.scala`,
  64 PC-indexed entries with a 64-bit datapath) mirroring the C# `StridePrefetcher` bit-for-bit. Multi-degree models
  (`rtl_mpf_shim.cpp`): the access edge loads an internal drain queue and the shim pops one address per clock — the
  first is a Chisel Jouppi stream-buffer prefetcher (`native/RtlFu/StreamPf.scala`, 4 streams × depth 8 with LRU
  allocation) mirroring the C# `StreamPrefetcher`, whose allocation burst issues 8 lines from a single access; its
  differential test interleaves more sequential walkers than there are stream buffers so LRU eviction is exercised,
  demanding identical counts and target sequences. Selectable per sweep config (`"rtl_prefetcher_lib"`).

Port contracts and C ABIs for wrapping further units are documented in `native/RtlFu/README.md`. Desktop-only
(`NativeLibrary`), like the CBP FFI predictors. All four surfaces are also reachable from `.csx`/`.fsx` architecture
scripts (the script hosts pre-import the RTL namespaces): `BranchPredictorFactory` on a pipeline spec,
`RtlBackedExecutor` around the mechanism factory's executor, and per-level `PolicyFactory`/`PrefetcherFactory` on
`CacheLevelSpec` — see `scripts/example-rtl.csx`, and `examples/rtl-machine/` for a self-contained
bring-your-own-RTL project: a custom Chisel ALU and branch predictor wired into an OoO train by an F# script whose
inline selector defines the instruction→opcode mapping, with no C#-side changes.

```bash
# Verilate the Chisel units, then drive all four surfaces from the pipeline
native/RtlFu/build.sh native/RtlFu/generated/DivUnit.sv DivUnit /tmp/rtl_div.so
native/RtlFu/build.sh native/RtlFu/generated/MulUnit.sv MulUnit /tmp/rtl_mul.so
native/RtlFu/build.sh native/RtlFu/generated/GshareBp.sv GshareBp /tmp/rtl_gshare.so rtl_bp_shim.cpp
native/RtlFu/build.sh native/RtlFu/generated/SrripRp.sv SrripRp /tmp/rtl_srrip.so rtl_rp_shim.cpp
native/RtlFu/build.sh native/RtlFu/generated/StridePf.sv StridePf /tmp/rtl_stride.so rtl_pf_shim.cpp
# ...then name them per sweep config in the JSON spec:
#   {"predictor": {"type": "rtl_bp_plugin", "library_path": "/tmp/rtl_gshare.so"},
#    "rtl_cache_policy_lib": "/tmp/rtl_srrip.so", "rtl_prefetcher_lib": "/tmp/rtl_stride.so",
#    "rtl_div_lib": "/tmp/rtl_div.so", "rtl_mul_lib": "/tmp/rtl_mul.so", "rtl_fdiv_lib": "/tmp/rtl_fdiv.so"}
dotnet run --project src/Apps/Runner -- prog.elf --sweep rtl.json
```

