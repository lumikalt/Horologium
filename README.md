# Horologium

[![CI](https://github.com/lumikalt/Horologium/actions/workflows/ci.yml/badge.svg)](https://github.com/lumikalt/Horologium/actions/workflows/ci.yml)

[▶ Face demo](docs/face.mp4)

Horologium simulates computer architectures: a discrete-event CPU pipeline simulator, written in C# targeting .NET
11, whose simulation engine is ISA-agnostic — concrete ISAs (RISC-V and several small teaching ISAs) plug in as
separate assemblies without modifying the engine. The goal is comparing hardware configurations (branch predictors,
caches, pipeline topologies) and generating measurement data for architecture research.

See [`docs/`](docs/) for the project's architecture, ISA coverage, and every subsystem's deep-dive documentation;
[docs/project-layout.md](docs/project-layout.md) covers how the codebase itself is organized.

## Running the UI

```bash
dotnet run --project src/Apps/Face      # RISC-V desktop UI
dotnet run --project src/Apps/Chip8Face # CHIP-8 desktop UI
```

See [docs/face-ui.md](docs/face-ui.md) for a tour of Face's tabs (workload/pipeline picker, Waveform, PEvents
waterfall, Assembler, Vector register file, and the gem5-style Configurator).

## Running the tests

```bash
dotnet test                                               # run all tests
dotnet test --filter "FullyQualifiedName~DecoderTests"    # one test class
dotnet test --filter "Name=SpecificTestMethod"            # one test method
dotnet test --filter "FullyQualifiedName!~BenchmarkTests" # skip the (slow) benchmark suite
```

The benchmark tests simulate fourteen ELF binaries on three pipeline configurations and can take 1–2 minutes in Debug.
See [docs/benchmarks.md](docs/benchmarks.md) for the workload list.

Correctness is also checked commit-for-commit against Spike, the reference RISC-V simulator
(`dotnet test --filter "FullyQualifiedName~SpikeCoSim"`); see [docs/cosim.md](docs/cosim.md) for the full contract.

## Running the simulator (CLI)

```bash
dotnet run --project src/Apps/Runner -- --help                              # CLI usage
dotnet run --project src/Apps/Runner -- TestBinaries/benchmarks/qsort.elf   # run an ELF under the default sweep
dotnet run --project src/Apps/Runner -- --script scripts/ooo.fsx prog.elf   # run against a scripted architecture
```

See [docs/runner-examples.md](docs/runner-examples.md) for worked examples of hardware-configuration sweeps, and
[docs/scripting-and-checkpointing.md](docs/scripting-and-checkpointing.md) for `.csx`/`.fsx` architecture scripting
and checkpointing.

## Development environment

Provided by `flake.nix` + `direnv`: the .NET 11 SDK, RISC-V cross-toolchains, Spike, and other tools used by the
tests and CLI above. `dotnet build`/`dotnet test` work without it for pure C# work.
The Nix shell is needed for anything that touches cross-compiled fixtures or Spike co-simulation.
