# Benchmark Workloads

Bare-metal RISC-V benchmark suites bundled under `TestBinaries/benchmarks`.

Thirteen bare-metal RISC-V benchmarks compiled from the riscv-tests suite: `dhrystone`, `gcd`, `median`, `memcpy`, `mm`,
`multiply`, `pchase`, `qsort`, `rsort`, `spmv`, `towers`, `treesum`, and `vvadd` (`dhrystone` self-times via `mcycle`;
`mm` and `spmv` use double-precision FP — all three became buildable once those features landed), plus **CoreMark** (
EEMBC, vendored under `TestBinaries/coremark/` with a bare-metal port in `coremark/port/`: mcycle timing, HTIF console
output, and the self-validation verdict surfaced as the exit code so the test gate catches CRC failures). The committed
`coremark.elf` runs 14 iterations (~4.4M retired instructions, ~10× the next-largest benchmark); rebuild with
`make benchmarks/coremark.elf COREMARK_ITERATIONS=n` for heavier runs. Also included are all 19 **Embench-IoT**
benchmarks (Patterson et al., the modern IoT benchmark suite replacing Dhrystone): `aha-mont64`, `crc32`, `depthconv`,
`edn`, `huffbench`, `matmult-int`, `md5sum`, `nettle-aes`, `nettle-sha256`, `nsichneu`, `picojpeg`, `qrduino`,
`sglib-combined`, `slre`, `statemate`, `tarfind`, `ud`, `wikisort`, and `xgboost`. They are built with the same
riscv-tests `crt.S`/`syscalls.c` environment and a bare-metal BSP under `TestBinaries/embench-iot/port/` (no-op
`start_trigger`/`stop_trigger`, inline `memcmp`/`strchr`/`memmove`/`sqrt`, and a bare-metal `ctype.h`). The key
compile-time requirement is `-fno-tree-loop-distribute-patterns`: without it GCC 15 converts the byte-by-byte fallback
in the syscalls.c `memset` into an infinite recursive call on unaligned inputs. They are built against `bmarks.ld` (same
HTIF exit protocol) and all pass under `BenchmarkTests`. All benchmarks are available as preset workloads in the Face UI
and can be passed to the Runner as ELF arguments. `BenchmarkTests` (`Tests/RiscV32/Analysis/BenchmarkTests.cs`) fans
each binary out across `Parallel.ForEach` rather than relying on xUnit's per-class serialization, since every run builds
its own memory image and train with no shared state. Note: benchmark tests are still the heaviest test set — run them
selectively with `--filter`.

ISA conformance tests (`TestBinaries/isa/`) are also linked at `0x80000000`. `FlatMemory` accepts an optional
`baseAddress` constructor parameter so the backing byte array starts at the first PT_LOAD segment (e.g. `0x80000000`)
rather than at address 0, avoiding a 2 GB allocation. `Rv32ElfWorkload.BaseAddress` exposes this value;
`IWorkload.BaseAddress` defaults to 0 for zero-based images.

