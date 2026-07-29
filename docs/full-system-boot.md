# Full-System Booting

OpenSBI and Linux NOMMU boot milestones and the peripheral bus behind them.

Two full-system boot milestones are verified by tests in `Tests/RiscV32/` and their RV64 counterparts in
`Tests/RiscV64/`:

- **OpenSBI v1.8** (`SingleCycle_OpenSBI_PrintsBanner`): `fw_jump.bin` (generic platform) boots on a `SingleCycleTrain`
  and prints its version banner on the ns16550a UART. RV32 built via `nix build .#opensbi-rv32`; RV64 (PIE
  `fw_jump.elf`, `riscv64-unknown-linux-gnu-` toolchain) via `nix build .#opensbi-rv64`.
- **Linux 6.12 NOMMU** (`SingleCycle_Linux_PrintsBanner`): a `nommu_virt_defconfig + M-mode` kernel loads at
  `0x80000000` (PAGE_OFFSET) and prints `Linux version …` via earlycon on the UART. No OpenSBI — the kernel runs
  entirely in M-mode, so it is launched directly. RV32 (`+ 32-bit.config` fragment) built via `nix build .#linux-rv32`;
  RV64 (`nommu_virt_defconfig` defaults to RV64) via `nix build .#linux-rv64`.

The peripheral bus is a `PeripheralBus` routing three devices: a `ClintDevice` (MTIP/MSIP at 0x02000000), a
`PlicDevice` (external interrupt routing at 0x0C000000), and an `Ns16550aUart` (ns16550a console at 0x10000000; TX
writes flush immediately to a `TextWriter`). `VirtDtb.Bytes` (`src/Isa/RiscV32/Memory/virt.dts`) and its RV64
counterpart `Rv64VirtDtb.Bytes` (`src/Isa/RiscV64/Memory/virt64.dts`, 2-cell addressing, `riscv,isa="rv64imafdc"`,
`mmu-type="riscv,sv39"`) are hand-crafted device tree blobs declaring 128 MiB RAM, all three devices, and one
`virtio_mmio` block device slot. The RV32 and RV64 OpenSBI/Linux `nix build` outputs must use distinct `-o` out-link
names (e.g. `result-opensbi-rv64`, `result-linux-rv64`) — otherwise building one clobbers the other's default `result`
symlink.

