# Co-Simulation

How Horologium cross-checks itself against a real RISC-V reference (Spike) at both the unit-test and
contribution-gate level.

## Spike lock-step co-simulation (src/Isa/RiscV32/CoSim)

`SpikeCoSimReference` implements `ICommitObserver` and launches Spike as a live child process with `--log-commits`. For
each instruction that Horologium commits, it reads the next line from Spike's stderr stream (blocking until Spike
produces it), then immediately compares PC, raw encoding, and any integer register write — divergence is reported at the
exact failing instruction. Boot-ROM commits (PC below `baseAddress`) are skipped. Attach it via the optional
`commitObserver` parameter to `SingleCycleTrain`, `FiveStageTrain`, or `OooeTrain` — the in-order pipeline fires
`OnCommit` from `WritebackStage` on normal retire, and the out-of-order pipeline fires once per ROB-head commit in
program order, so the check covers the hazard/forwarding and speculative-memory datapaths too. Wrap in `using` to kill
Spike on completion. `SpikeCoSimTests` runs three fixtures across all three trains: `test.elf` (simple RV32I golden
path), `rich.elf` (RV32IM — multiply/divide, an insertion sort, and heavy data-dependent branching, to exercise the
multi-cycle functional units, store-to-load forwarding, and flush paths), and `htif.elf` (the same RV32IM workload but
terminating through the HTIF `tohost` register instead of EBREAK, so standalone Spike exits cleanly). Spike logs the
post-exit spin-loop an indeterminate number of times; each train commits up to the exit, then halts, so its stream is a
clean prefix of Spike's. `dtc` must be on PATH (the Nix dev-shell provides it); the tests can be excluded from CI
without Spike with `--filter "FullyQualifiedName!~SpikeCoSim"`.

## Co-simulation contract

Spike is the reference of record for ISA correctness. The contract: **every change to the decoder, executor,
register/CSR/trap state, or any train's commit path must keep `SpikeCoSimTests` green.** Those tests run all three
trains (`SingleCycleTrain`, `FiveStageTrain`, `OooeTrain`) against `test.elf`, `rich.elf`, and `htif.elf`, comparing
every committed instruction's PC, encoding, and integer register writes to Spike commit-for-commit (see *Spike
lock-step co-simulation* above). A green run means the simulated datapath agrees with a real RISC-V reference
instruction-by-instruction — the strongest correctness signal in the project.

Beyond the three hand-written fixtures, `SingleCycle_Conformance_MatchesSpike` co-simulates all 71 official
`riscv-tests` `rv32ui`/`rv32um`/`rv32ua`/`rv32uc`/`rv32uf` ELFs (already shipped under `TestBinaries/isa/`)
commit-for-commit — per-instruction verification on top of the self-checking `RiscVTestSuiteTests`, which only inspect
the final `gp` pass code. (`ma_data` is excluded: it tests misaligned access, which Spike traps and a handler fixes up
while Horologium's `FlatMemory` permits directly, so the two diverge by design.)

If you add an instruction, extension, pipeline behaviour, or fixture, add or extend a co-sim fixture so the new path is
covered, and run:

```bash
dotnet test Tests/ --filter "FullyQualifiedName~SpikeCoSim"                            # run the co-sim contract
HOROLOGIUM_REQUIRE_COSIM=1 dotnet test Tests/ --filter "FullyQualifiedName~SpikeCoSim" # CI mode: missing toolchain → failure
```

**Toolchain.** The tests need `spike` and `dtc`; the Nix dev-shell (`flake.nix` + `direnv`) provides both. When the
toolchain is absent the tests **skip** rather than fail, so the suite stays runnable everywhere. Set *
*`HOROLOGIUM_REQUIRE_COSIM=1`** to flip a missing toolchain into a hard failure so the contract cannot be satisfied by
silently skipping. The GitHub Actions workflow (`.github/workflows/ci.yml`) enforces exactly this on every pull request
and every push to `trunk`, running the full suite (benchmarks excluded) in the flake's lightweight `ci` dev shell.
ISA-correctness coverage that does *not* need Spike (e.g. HTIF termination, the official `riscv-tests` self-checks)
lives in `HtifExitTests` / `RiscVTestSuiteTests` and always runs.

> Note: Spike sees only the standard ISA. UVE and other custom extensions are invisible to it, so their correctness is
> covered by Horologium's own integration tests, not co-sim.
