# CoreMark (vendored)

Core benchmark files vendored unmodified from
https://github.com/eembc/coremark at commit
`1f483d5b8316753a742cbf5590caf5bd0a4e4777`, under the Apache-2.0 license
(see `LICENSE.md`). Per the COREMARK acceptable-use agreement, official
CoreMark *scores* may only be reported from unmodified source built per the
run rules — the port here is for microarchitecture simulation workloads,
not score publication.

`port/` contains the Horologium bare-metal port (`core_portme.{h,c}`,
`ee_printf.c`), adapted from the upstream `barebones/` template:

- timing via the `mcycle` CSR,
- `ee_printf` formatting from the upstream barebones implementation, with
  `uart_send_char` routed to the riscv-tests HTIF console `putchar`
  (`benchmarks/common/syscalls.c`, unmodified — it is a submodule),
- `HAS_FLOAT=0`,
- validation surfaced as the process exit code: `ee_printf` watches for the
  "Correct operation validated" verdict and `portable_fini` exits nonzero if
  it never appeared, so BenchmarkTests' tohost gate catches CRC failures.

Built by `TestBinaries/Makefile` into `benchmarks/coremark.elf`; iteration
count is set at compile time via `COREMARK_ITERATIONS`.
