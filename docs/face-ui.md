# Face UI Feature Reference

Detailed tour of `src/Apps/Face`'s tabs and views. See [project-layout.md](project-layout.md) for the one-paragraph
summary.

Avalonia desktop UI, RISC-V exclusive (CHIP-8 has its own app, **Chip8Face**). Opens directly into the RV32/RV64
view — an RV32/RV64 selector and a workload preset picker (RV64 offers only the built-in demo and a custom ELF path,
since no RV64 benchmark ELFs are bundled), a **multi-hart mode** toggle that swaps the single-config sweep for N harts
(each with its own pipeline/predictor config, an optional private cache, and a memory pool id), running a fixed,
hand-verified LR/SC atomic-increment demo program — the only workload safe to share across harts, since real ELFs
assume a single, non-shared stack — with harts sharing a pool id sharing one coherent memory/bus/shared-LLC domain and
harts in different pools fully isolated from each other, with results rendering as one row per hart in the same
Chart/Table view, a **Waveform tab** that plots selected signals over simulation time from the run's periodic snapshots
(per-window counter deltas plus derived windowed IPC and cache hit rates, one line per config × signal), a **PEvents
tab** with a scrollable Argos-style pipeline waterfall (rows = instructions, columns = cycles, cells = stage
abbreviation F/DC/D/IS/EX/RT/FL), a **SpecPC** gutter column showing the fetch-window start address,
flush/misprediction cycles highlighted red, fetch-stall cycles dimmed, and an **Assembler tab** with a three-pane RISC-V
assembly editor (editor + decoded listing + register file), and a **Vector tab** showing the v0-v31 vector register
file (VLEN=128) as a grid with a selectable e8/e16/e32/e64 element-width view and a live vtype/vl readout. The Assembler
tab has a sidebar **language toggle (RISC-V ASM / C)**: in C mode the source is compiled with `riscv32-none-elf-gcc` (
selectable `-O` level) against a tiny `_start` stub, the resulting `.text` is disassembled into the listing, and
single-cycle stepping highlights the current C source line via `objdump -dl` line info. The Assembler tab's own
sub-tab strip has a second entry, **UVE Kernel**, alongside its CPU editor: a source editor and console-output panel
for a real, compiled UVE kernel — `CompiledSourceWorkload` shells out to a user-supplied patched UVE clang path
(github.com/lumicrespo/UVEcompiler — not bundled by `flake.nix`) plus the flake's `riscv64-unknown-linux-musl-gcc` as
link driver, wraps the result as a real linked ELF workload, and runs it via `Experiment.RunLinkedElf` (psABI stack +
`LinuxSyscallEmulator`, so `printf` works) under a single selected "ooo" config (the sidebar's own config picker,
restricted to that pipeline — the only one that steps the `StreamingEngine` UVE instructions need). A
**Configurator tab** is a
gem5-style architecture builder: an AvaloniaEdit pane edits a `.csx` script (the same `Script.ScriptHost`/
`Pipeline.Spec.MachineSpec` API `Runner --script` uses) and hot-reloads it on both in-app edits (debounced) and external
saves (`FileSystemWatcher`), against a workload preset picker, with a live cache/TLB/dial stat panel while running — a
separate, coexisting path from the `ConfigViewModel`/`TrainConfig` GUI knobs the other tabs use.
