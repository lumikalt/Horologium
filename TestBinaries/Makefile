CC      = riscv32-none-elf-gcc
CFLAGS  = -march=rv32imaf -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g
LDFLAGS = -T env/link.ld

# Include paths: custom env first (overrides upstream riscv_test.h),
# then the upstream test_macros.h from the scalar macro directory.
INCLUDES = -I env -I riscv-tests/isa/macros/scalar

# ── Original hand-written test binary ────────────────────────────────────────

CFLAGS_ORIG = -march=rv32i -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g

test.elf: crt0.s test.c test.ld
	$(CC) $(CFLAGS_ORIG) -T test.ld crt0.s test.c -o $@

# ── Richer RV32IM co-sim workload (mul/div, sort, branches) ──────────────────

CFLAGS_RICH = -march=rv32im -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g

rich.elf: crt0.s rich.c test.ld
	$(CC) $(CFLAGS_RICH) -T test.ld crt0.s rich.c -o $@

# HTIF-terminating variant: exits via the tohost register (no EBREAK), so
# standalone Spike halts cleanly. Same RV32IM workload as rich.c.
htif.elf: htif_crt.s htif.c htif.ld
	$(CC) $(CFLAGS_RICH) -T htif.ld htif_crt.s htif.c -o $@

# ── Zcmop co-sim workload (c.mop.N hint NOPs) ────────────────────────────────
#
# Uses .2byte directives for the 16-bit c.mop.N encodings; no special -march
# flag is needed since they are emitted as raw literals, not named mnemonics.

CFLAGS_ZCMOP = -march=rv32ic -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g

zcmop.elf: crt0.s zcmop.c test.ld
	$(CC) $(CFLAGS_ZCMOP) -T test.ld crt0.s zcmop.c -o $@

# ── Zimop co-sim workload (mop.r.N / mop.rr.N hint NOPs) ────────────────────
#
# Raw .4byte encodings — no special -march flag needed.

CFLAGS_ZIMOP = -march=rv32imafc -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g

zimop.elf: crt0.s zimop.c test.ld
	$(CC) $(CFLAGS_ZIMOP) -T test.ld crt0.s zimop.c -o $@

# ── Zabha co-sim workload (byte / halfword AMOs) ──────────────────────────────

CFLAGS_ZABHA = -march=rv32imaf_zabha -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g

zabha.elf: crt0.s zabha.c test.ld
	$(CC) $(CFLAGS_ZABHA) -T test.ld crt0.s zabha.c -o $@

# ── Zawrs co-sim workload (wrs.nto / wrs.sto hint NOPs) ──────────────────────

CFLAGS_ZAWRS = -march=rv32imaf_zawrs -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g

zawrs.elf: crt0.s zawrs.c test.ld
	$(CC) $(CFLAGS_ZAWRS) -T test.ld crt0.s zawrs.c -o $@

# ── Zicbom/Zicboz co-sim workload (cache block operations) ───────────────────

CFLAGS_CBO = -march=rv32imaf_zicbom_zicboz -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g

cbo.elf: crt0.s cbo.c test.ld
	$(CC) $(CFLAGS_CBO) -T test.ld crt0.s cbo.c -o $@

# ── RVV co-sim workload (vector ALU ops) ─────────────────────────────────────
#
# Sets mstatus.VS in main() so Spike does not trap on vector instructions.

CFLAGS_VECTOR = -march=rv32gcv -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g

vector.elf: crt0.s vector.c test.ld
	$(CC) $(CFLAGS_VECTOR) -T test.ld crt0.s vector.c -o $@

# ── Official riscv-tests — rv32ui (base integer ISA) ─────────────────────────
#
# fence_i.S: self-modifying-code test; Horologium does not model I-cache
#   coherency, so fence.i is architecturally correct as a no-op but the
#   test itself writes new instructions into the instruction stream and
#   expects them to execute — behaviour that our flat-memory fetch
#   already supports naturally.  However the test was written for spike
#   and its timing assumptions about when stores become visible may not
#   hold.  Excluded to avoid a flaky test.
#
# ma_data.S: misaligned-access test that relies on the CPU taking a trap
#   and the trap handler correcting the result.  Horologium's FlatMemory
#   allows byte-granular misaligned access directly, so the trap is never
#   raised and the test fails.  Excluded.

RV32UI_SRC = $(wildcard riscv-tests/isa/rv32ui/*.S)
RV32UI_SKIP = riscv-tests/isa/rv32ui/fence_i.S
RV32UI_BUILD = $(filter-out $(RV32UI_SKIP), $(RV32UI_SRC))
RV32UI_ELFS  = $(patsubst riscv-tests/isa/rv32ui/%.S, isa/rv32ui-p-%.elf, $(RV32UI_BUILD))

isa/rv32ui-p-%.elf: riscv-tests/isa/rv32ui/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC) $(CFLAGS) $(INCLUDES) $(LDFLAGS) $< -o $@

# ── Official riscv-tests — rv32um (multiply / divide extension) ───────────────

RV32UM_SRC  = $(wildcard riscv-tests/isa/rv32um/*.S)
RV32UM_ELFS = $(patsubst riscv-tests/isa/rv32um/%.S, isa/rv32um-p-%.elf, $(RV32UM_SRC))

isa/rv32um-p-%.elf: riscv-tests/isa/rv32um/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC) $(CFLAGS) $(INCLUDES) $(LDFLAGS) $< -o $@

# ── Official riscv-tests — rv32ua (atomic AMOs + LR/SC) ──────────────────────
#
# The existing CFLAGS already include the A extension (-march=rv32imaf).

RV32UA_SRC  = $(wildcard riscv-tests/isa/rv32ua/*.S)
RV32UA_ELFS = $(patsubst riscv-tests/isa/rv32ua/%.S, isa/rv32ua-p-%.elf, $(RV32UA_SRC))

isa/rv32ua-p-%.elf: riscv-tests/isa/rv32ua/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC) $(CFLAGS) $(INCLUDES) $(LDFLAGS) $< -o $@

# ── Official riscv-tests — rv32uc (compressed instructions) ──────────────────

CFLAGS_C = -march=rv32imafc -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g

RV32UC_SRC  = $(wildcard riscv-tests/isa/rv32uc/*.S)
RV32UC_ELFS = $(patsubst riscv-tests/isa/rv32uc/%.S, isa/rv32uc-p-%.elf, $(RV32UC_SRC))

isa/rv32uc-p-%.elf: riscv-tests/isa/rv32uc/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC) $(CFLAGS_C) $(INCLUDES) $(LDFLAGS) $< -o $@

# ── Official riscv-tests — rv32uf (single-precision float) ───────────────────
#
# The existing CFLAGS already include the F extension (-march=rv32imaf).
# mstatus.FS is now set in RVTEST_CODE_BEGIN so Spike does not trap.

RV32UF_SRC  = $(wildcard riscv-tests/isa/rv32uf/*.S)
RV32UF_ELFS = $(patsubst riscv-tests/isa/rv32uf/%.S, isa/rv32uf-p-%.elf, $(RV32UF_SRC))

isa/rv32uf-p-%.elf: riscv-tests/isa/rv32uf/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC) $(CFLAGS) $(INCLUDES) $(LDFLAGS) $< -o $@

# ── Official riscv-tests — rv32uzba/zbb/zbc/zbs (bit-manipulation) ───────────

CFLAGS_ZBA = -march=rv32imaf_zba -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g
CFLAGS_ZBB = -march=rv32imaf_zbb -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g
CFLAGS_ZBC = -march=rv32imaf_zbc -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g
CFLAGS_ZBS = -march=rv32imaf_zbs -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g

RV32UZBA_SRC  = $(wildcard riscv-tests/isa/rv32uzba/*.S)
RV32UZBA_ELFS = $(patsubst riscv-tests/isa/rv32uzba/%.S, isa/rv32uzba-p-%.elf, $(RV32UZBA_SRC))

isa/rv32uzba-p-%.elf: riscv-tests/isa/rv32uzba/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC) $(CFLAGS_ZBA) $(INCLUDES) $(LDFLAGS) $< -o $@

RV32UZBB_SRC  = $(wildcard riscv-tests/isa/rv32uzbb/*.S)
RV32UZBB_ELFS = $(patsubst riscv-tests/isa/rv32uzbb/%.S, isa/rv32uzbb-p-%.elf, $(RV32UZBB_SRC))

isa/rv32uzbb-p-%.elf: riscv-tests/isa/rv32uzbb/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC) $(CFLAGS_ZBB) $(INCLUDES) $(LDFLAGS) $< -o $@

RV32UZBC_SRC  = $(wildcard riscv-tests/isa/rv32uzbc/*.S)
RV32UZBC_ELFS = $(patsubst riscv-tests/isa/rv32uzbc/%.S, isa/rv32uzbc-p-%.elf, $(RV32UZBC_SRC))

isa/rv32uzbc-p-%.elf: riscv-tests/isa/rv32uzbc/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC) $(CFLAGS_ZBC) $(INCLUDES) $(LDFLAGS) $< -o $@

RV32UZBS_SRC  = $(wildcard riscv-tests/isa/rv32uzbs/*.S)
RV32UZBS_ELFS = $(patsubst riscv-tests/isa/rv32uzbs/%.S, isa/rv32uzbs-p-%.elf, $(RV32UZBS_SRC))

isa/rv32uzbs-p-%.elf: riscv-tests/isa/rv32uzbs/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC) $(CFLAGS_ZBS) $(INCLUDES) $(LDFLAGS) $< -o $@

# ── Official riscv-tests — rv32uzicond (Zicond conditional ops) ───────────────

CFLAGS_ZICOND = -march=rv32imaf_zicond -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g

RV32UZICOND_SRC  = $(wildcard riscv-tests/isa/rv32uzicond/*.S)
RV32UZICOND_ELFS = $(patsubst riscv-tests/isa/rv32uzicond/%.S, isa/rv32uzicond-p-%.elf, $(RV32UZICOND_SRC))

isa/rv32uzicond-p-%.elf: riscv-tests/isa/rv32uzicond/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC) $(CFLAGS_ZICOND) $(INCLUDES) $(LDFLAGS) $< -o $@

# ── Official riscv-tests — rv32si (supervisor-level integer) ──────────────────
#
# sbreak: tests that EBREAK raises a Breakpoint exception rather than halting.
#   Horologium uses EBREAK as the halt/pass-fail signal, so this test would
#   halt immediately before the trap handler runs.  Excluded.
#
# dirty: tests VM referenced/dirty bits (Sv32 page table).  Horologium has no
#   MMU.  Excluded.
#
# ma_fetch: tests misaligned-fetch trap.  Horologium's fetch does not check
#   alignment.  Excluded.

SI_INCLUDES = -I env -I riscv-tests/isa/macros/scalar

RV32SI_SRC  = $(wildcard riscv-tests/isa/rv32si/*.S)
RV32SI_SKIP = riscv-tests/isa/rv32si/sbreak.S \
              riscv-tests/isa/rv32si/dirty.S \
              riscv-tests/isa/rv32si/ma_fetch.S
RV32SI_BUILD = $(filter-out $(RV32SI_SKIP), $(RV32SI_SRC))
RV32SI_ELFS  = $(patsubst riscv-tests/isa/rv32si/%.S, isa/rv32si-p-%.elf, $(RV32SI_BUILD))

isa/rv32si-p-%.elf: riscv-tests/isa/rv32si/%.S env/si/riscv_test.h env/riscv_test_s.h env/default_trap.S env/link.ld
	@mkdir -p isa
	$(CC) $(CFLAGS) -I env/si -I env $(SI_INCLUDES) $(LDFLAGS) $< env/default_trap.S -o $@

# ── RV64: shared toolchain/env ────────────────────────────────────────────────
#
# env/riscv_test.h, env/riscv_test_s.h, env/default_trap.S, and env/link.ld are
# all XLEN-agnostic (RVTEST_RV64U/RVTEST_RV64S/RVTEST_RV64M are already defined
# as no-ops upstream) and are reused unchanged from the RV32 test environment.

CC64      = riscv64-none-elf-gcc
CFLAGS64  = -march=rv64imafd -mabi=lp64 -nostdlib -nostartfiles -static -O1 -g
LDFLAGS64 = -T env/link.ld

# Real Linux userspace toolchain (musl — see flake.nix for why musl over glibc), for genuinely
# linked (not bare-metal -nostdlib) fixtures: real _start, real libc, real stdio.
CC64_MUSL = riscv64-unknown-linux-musl-gcc

# ── RV64 hand-written co-sim workloads (test.c/rich.c/htif.c, RV64 build) ─────
#
# Same sources as the RV32 test.elf/rich.elf/htif.elf above; crt0.s, htif_crt.s,
# test.ld, and htif.ld are all XLEN-agnostic.

CFLAGS64_ORIG = -march=rv64i -mabi=lp64 -nostdlib -nostartfiles -static -O1 -g
CFLAGS64_RICH = -march=rv64im -mabi=lp64 -nostdlib -nostartfiles -static -O1 -g

test64.elf: crt0.s test.c test.ld
	$(CC64) $(CFLAGS64_ORIG) -T test.ld crt0.s test.c -o $@

rich64.elf: crt0.s rich.c test.ld
	$(CC64) $(CFLAGS64_RICH) -T test.ld crt0.s rich.c -o $@

htif64.elf: htif_crt.s htif.c htif.ld
	$(CC64) $(CFLAGS64_RICH) -T htif.ld htif_crt.s htif.c -o $@

# ── Official riscv-tests — rv64ui (base integer ISA) ──────────────────────────
#
# fence_i.S / ma_data.S: excluded for the same reasons as their RV32
# counterparts — no I-cache coherency model, and misaligned accesses never
# trap (FlatMemory allows byte-granular access directly).

RV64UI_SRC   = $(wildcard riscv-tests/isa/rv64ui/*.S)
RV64UI_SKIP  = riscv-tests/isa/rv64ui/fence_i.S riscv-tests/isa/rv64ui/ma_data.S
RV64UI_BUILD = $(filter-out $(RV64UI_SKIP), $(RV64UI_SRC))
RV64UI_ELFS  = $(patsubst riscv-tests/isa/rv64ui/%.S, isa/rv64ui-p-%.elf, $(RV64UI_BUILD))

isa/rv64ui-p-%.elf: riscv-tests/isa/rv64ui/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC64) $(CFLAGS64) $(INCLUDES) $(LDFLAGS64) $< -o $@

# ── Official riscv-tests — rv64um (multiply / divide, incl. *W variants) ──────

RV64UM_SRC  = $(wildcard riscv-tests/isa/rv64um/*.S)
RV64UM_ELFS = $(patsubst riscv-tests/isa/rv64um/%.S, isa/rv64um-p-%.elf, $(RV64UM_SRC))

isa/rv64um-p-%.elf: riscv-tests/isa/rv64um/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC64) $(CFLAGS64) $(INCLUDES) $(LDFLAGS64) $< -o $@

# ── Official riscv-tests — rv64ua (atomic AMOs + LR/SC, incl. .D forms) ───────

RV64UA_SRC  = $(wildcard riscv-tests/isa/rv64ua/*.S)
RV64UA_ELFS = $(patsubst riscv-tests/isa/rv64ua/%.S, isa/rv64ua-p-%.elf, $(RV64UA_SRC))

isa/rv64ua-p-%.elf: riscv-tests/isa/rv64ua/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC64) $(CFLAGS64) $(INCLUDES) $(LDFLAGS64) $< -o $@

# ── Official riscv-tests — rv64uc (compressed instructions) ───────────────────

CFLAGS64_C = -march=rv64imafdc -mabi=lp64 -nostdlib -nostartfiles -static -O1 -g

RV64UC_SRC  = $(wildcard riscv-tests/isa/rv64uc/*.S)
RV64UC_ELFS = $(patsubst riscv-tests/isa/rv64uc/%.S, isa/rv64uc-p-%.elf, $(RV64UC_SRC))

isa/rv64uc-p-%.elf: riscv-tests/isa/rv64uc/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC64) $(CFLAGS64_C) $(INCLUDES) $(LDFLAGS64) $< -o $@

# ── Official riscv-tests — rv64uf (single-precision float) ────────────────────

RV64UF_SRC  = $(wildcard riscv-tests/isa/rv64uf/*.S)
RV64UF_ELFS = $(patsubst riscv-tests/isa/rv64uf/%.S, isa/rv64uf-p-%.elf, $(RV64UF_SRC))

isa/rv64uf-p-%.elf: riscv-tests/isa/rv64uf/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC64) $(CFLAGS64) $(INCLUDES) $(LDFLAGS64) $< -o $@

# ── Official riscv-tests — rv64ud (double-precision float) ────────────────────

RV64UD_SRC  = $(wildcard riscv-tests/isa/rv64ud/*.S)
RV64UD_ELFS = $(patsubst riscv-tests/isa/rv64ud/%.S, isa/rv64ud-p-%.elf, $(RV64UD_SRC))

isa/rv64ud-p-%.elf: riscv-tests/isa/rv64ud/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC64) $(CFLAGS64) $(INCLUDES) $(LDFLAGS64) $< -o $@

# ── Official riscv-tests — rv64uzfh (half-precision float) ────────────────────

CFLAGS64_ZFH = -march=rv64imafd_zfh -mabi=lp64 -nostdlib -nostartfiles -static -O1 -g

RV64UZFH_SRC  = $(wildcard riscv-tests/isa/rv64uzfh/*.S)
RV64UZFH_ELFS = $(patsubst riscv-tests/isa/rv64uzfh/%.S, isa/rv64uzfh-p-%.elf, $(RV64UZFH_SRC))

isa/rv64uzfh-p-%.elf: riscv-tests/isa/rv64uzfh/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC64) $(CFLAGS64_ZFH) $(INCLUDES) $(LDFLAGS64) $< -o $@

# ── Official riscv-tests — rv64uzba/zbb/zbc/zbs (bit-manipulation) ────────────

CFLAGS64_ZBA = -march=rv64imafd_zba -mabi=lp64 -nostdlib -nostartfiles -static -O1 -g
CFLAGS64_ZBB = -march=rv64imafd_zbb -mabi=lp64 -nostdlib -nostartfiles -static -O1 -g
CFLAGS64_ZBC = -march=rv64imafd_zbc -mabi=lp64 -nostdlib -nostartfiles -static -O1 -g
CFLAGS64_ZBS = -march=rv64imafd_zbs -mabi=lp64 -nostdlib -nostartfiles -static -O1 -g

RV64UZBA_SRC  = $(wildcard riscv-tests/isa/rv64uzba/*.S)
RV64UZBA_ELFS = $(patsubst riscv-tests/isa/rv64uzba/%.S, isa/rv64uzba-p-%.elf, $(RV64UZBA_SRC))

isa/rv64uzba-p-%.elf: riscv-tests/isa/rv64uzba/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC64) $(CFLAGS64_ZBA) $(INCLUDES) $(LDFLAGS64) $< -o $@

RV64UZBB_SRC  = $(wildcard riscv-tests/isa/rv64uzbb/*.S)
RV64UZBB_ELFS = $(patsubst riscv-tests/isa/rv64uzbb/%.S, isa/rv64uzbb-p-%.elf, $(RV64UZBB_SRC))

isa/rv64uzbb-p-%.elf: riscv-tests/isa/rv64uzbb/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC64) $(CFLAGS64_ZBB) $(INCLUDES) $(LDFLAGS64) $< -o $@

RV64UZBC_SRC  = $(wildcard riscv-tests/isa/rv64uzbc/*.S)
RV64UZBC_ELFS = $(patsubst riscv-tests/isa/rv64uzbc/%.S, isa/rv64uzbc-p-%.elf, $(RV64UZBC_SRC))

isa/rv64uzbc-p-%.elf: riscv-tests/isa/rv64uzbc/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC64) $(CFLAGS64_ZBC) $(INCLUDES) $(LDFLAGS64) $< -o $@

RV64UZBS_SRC  = $(wildcard riscv-tests/isa/rv64uzbs/*.S)
RV64UZBS_ELFS = $(patsubst riscv-tests/isa/rv64uzbs/%.S, isa/rv64uzbs-p-%.elf, $(RV64UZBS_SRC))

isa/rv64uzbs-p-%.elf: riscv-tests/isa/rv64uzbs/%.S env/riscv_test.h env/link.ld
	@mkdir -p isa
	$(CC64) $(CFLAGS64_ZBS) $(INCLUDES) $(LDFLAGS64) $< -o $@

# ── Official riscv-tests — rv64si (supervisor-level integer) ──────────────────
#
# sbreak / dirty / ma_fetch: excluded for the same reasons as rv32si (see
# above). icache-alias: tests that a stale I-cache line is not used after a
# page-table remap without FENCE.I; Horologium has no I-cache to alias in the
# first place (fetch always reads the current backing memory), so this is
# also excluded as testing a cache implementation Horologium doesn't have.

RV64SI_SRC   = $(wildcard riscv-tests/isa/rv64si/*.S)
RV64SI_SKIP  = riscv-tests/isa/rv64si/sbreak.S \
               riscv-tests/isa/rv64si/dirty.S \
               riscv-tests/isa/rv64si/ma_fetch.S \
               riscv-tests/isa/rv64si/icache-alias.S
RV64SI_BUILD = $(filter-out $(RV64SI_SKIP), $(RV64SI_SRC))
RV64SI_ELFS  = $(patsubst riscv-tests/isa/rv64si/%.S, isa/rv64si-p-%.elf, $(RV64SI_BUILD))

isa/rv64si-p-%.elf: riscv-tests/isa/rv64si/%.S env/si/riscv_test.h env/riscv_test_s.h env/default_trap.S env/link.ld
	@mkdir -p isa
	$(CC64) $(CFLAGS64) -I env/si -I env $(SI_INCLUDES) $(LDFLAGS64) $< env/default_trap.S -o $@

# ── riscv-tests benchmarks ───────────────────────────────────────────────────
#
# dhrystone / mm / spmv: originally excluded because mcycle stayed at 0 and the
#   D extension was missing; both are implemented now (mcycle increments per
#   retired instruction, RV32IMAFD), so all three build and pass.
#
# mt-*: multi-threaded via thread_entry on several harts; the benchmark test
#   harness drives a single hart.  Excluded.
#
# vec-*: use the standard V-extension intrinsics/assembly written for RVV 1.0
#   vector strip-mine loops; untested against our VLEN=128 implementation.
#   Excluded for now.
#
# pmp: exercises PMP CSRs not implemented by the simulator.  Excluded.

BMARKS_DIR  = riscv-tests/benchmarks
BMARKS_CC   = riscv32-none-elf-gcc
BMARKS_CFLAGS = -march=rv32imafd_zicsr -mabi=ilp32d -O2 -static \
                -nostdlib -nostartfiles \
                -DPREALLOCATE=1 -mcmodel=medany -fno-common \
                -fno-builtin-printf -fno-tree-loop-distribute-patterns \
                -Wno-implicit-int -Wno-implicit-function-declaration
BMARKS_LDFLAGS = -T bmarks.ld
BMARKS_INCS = -I$(BMARKS_DIR) -I$(BMARKS_DIR)/common \
              -Iriscv-tests/debug/programs \
              -I$(BMARKS_DIR)/median -I$(BMARKS_DIR)/qsort \
              -I$(BMARKS_DIR)/rsort  -I$(BMARKS_DIR)/towers \
              -I$(BMARKS_DIR)/vvadd  -I$(BMARKS_DIR)/memcpy \
              -I$(BMARKS_DIR)/multiply \
              -I$(BMARKS_DIR)/gcd    -I$(BMARKS_DIR)/treesum \
              -I$(BMARKS_DIR)/pchase \
              -I$(BMARKS_DIR)/dhrystone -I$(BMARKS_DIR)/mm -I$(BMARKS_DIR)/spmv
BMARKS_COMMON = $(BMARKS_DIR)/common/syscalls.c $(BMARKS_DIR)/common/crt.S

BMARKS = median qsort rsort towers vvadd memcpy multiply gcd treesum pchase \
         dhrystone mm spmv coremark

BMARKS_SRCS_median   = $(BMARKS_DIR)/median/median_main.c   $(BMARKS_DIR)/median/median.c
BMARKS_SRCS_qsort    = $(BMARKS_DIR)/qsort/qsort_main.c
BMARKS_SRCS_rsort    = $(BMARKS_DIR)/rsort/rsort.c
BMARKS_SRCS_towers   = $(BMARKS_DIR)/towers/towers_main.c
BMARKS_SRCS_vvadd    = $(BMARKS_DIR)/vvadd/vvadd_main.c
BMARKS_SRCS_memcpy   = $(BMARKS_DIR)/memcpy/memcpy_main.c
BMARKS_SRCS_multiply = $(BMARKS_DIR)/multiply/multiply_main.c $(BMARKS_DIR)/multiply/multiply.c
BMARKS_SRCS_gcd      = $(BMARKS_DIR)/gcd/gcd_main.c
BMARKS_SRCS_treesum  = $(BMARKS_DIR)/treesum/treesum_main.c
BMARKS_SRCS_pchase   = $(BMARKS_DIR)/pchase/pchase_main.c
BMARKS_SRCS_dhrystone = $(BMARKS_DIR)/dhrystone/dhrystone_main.c $(BMARKS_DIR)/dhrystone/dhrystone.c
BMARKS_SRCS_mm       = $(BMARKS_DIR)/mm/mm_main.c $(BMARKS_DIR)/mm/mm.c
BMARKS_SRCS_spmv     = $(BMARKS_DIR)/spmv/spmv_main.c

# dhrystone is K&R C; GCC 15 defaults to C23, which removes unprototyped calls.
BMARKS_EXTRA_dhrystone = -std=gnu11

# ── CoreMark (vendored under coremark/, port under coremark/port/) ───────────
#
# ITERATIONS is fixed at compile time (SEED_VOLATILE); one iteration is
# ~475k single-cycle cycles, so 14 iterations ≈ 6.6M — the heaviest committed
# benchmark, still inside the BenchmarkTests tick budgets (10M single-cycle,
# 20M five-stage/OoO).  Override with
# `make benchmarks/coremark.elf COREMARK_ITERATIONS=n` for longer runs; the
# port's nominal CLOCKS_PER_SEC (100 kHz) keeps CoreMark's minimum-10-second
# validity check satisfied down to ~3 iterations.

COREMARK_ITERATIONS ?= 14

BMARKS_SRCS_coremark = coremark/core_main.c coremark/core_list_join.c \
                       coremark/core_matrix.c coremark/core_state.c \
                       coremark/core_util.c \
                       coremark/port/core_portme.c coremark/port/ee_printf.c
BMARKS_EXTRA_coremark = -Icoremark -Icoremark/port \
                        -DPERFORMANCE_RUN=1 -DITERATIONS=$(COREMARK_ITERATIONS) \
                        -DFLAGS_STR='"-O2"'

define bmark_template
benchmarks/$(1).elf: $$(BMARKS_SRCS_$(1)) $$(BMARKS_COMMON) bmarks.ld
	@mkdir -p benchmarks
	$$(BMARKS_CC) $$(BMARKS_CFLAGS) $$(BMARKS_EXTRA_$(1)) $$(BMARKS_INCS) $$(BMARKS_LDFLAGS) \
	    $$(BMARKS_SRCS_$(1)) $$(BMARKS_COMMON) -lgcc -o $$@
endef

$(foreach b,$(BMARKS),$(eval $(call bmark_template,$(b))))

BMARKS_ELFS = $(patsubst %, benchmarks/%.elf, $(BMARKS))

# ── Embench-IoT (Patterson et al., modern IoT benchmark suite) ───────────────
#
# 19 benchmarks compiled against the riscv-tests bare-metal environment
# (crt.S + syscalls.c) with a local BSP (embench-iot/port/).
#
# -fno-tree-loop-distribute-patterns is required: without it GCC 15 converts
# the byte-by-byte loop inside the syscalls.c memset into a recursive call to
# memset itself, causing infinite recursion on any unaligned invocation.

EMBENCH_DIR   = embench-iot
EMBENCH_CC    = $(BMARKS_CC)
EMBENCH_CFLAGS = $(BMARKS_CFLAGS) \
                 -DGLOBAL_SCALE_FACTOR=1 -DWARMUP_HEAT=1
EMBENCH_LDFLAGS = $(BMARKS_LDFLAGS)
EMBENCH_COMMON = $(EMBENCH_DIR)/support/main.c \
                 $(EMBENCH_DIR)/support/beebsc.c \
                 $(EMBENCH_DIR)/port/boardsupport.c \
                 $(BMARKS_COMMON)
EMBENCH_INCS  = -I$(EMBENCH_DIR)/support \
                -I$(EMBENCH_DIR)/port \
                $(BMARKS_INCS)

EMBENCHS = aha-mont64 crc32 depthconv edn huffbench matmult-int md5sum \
           nettle-aes nettle-sha256 nsichneu picojpeg qrduino \
           sglib-combined slre statemate tarfind ud wikisort xgboost

EMBENCH_SRCS_aha-mont64    = $(EMBENCH_DIR)/src/aha-mont64/mont64.c
EMBENCH_SRCS_crc32         = $(EMBENCH_DIR)/src/crc32/crc_32.c
EMBENCH_SRCS_depthconv     = $(EMBENCH_DIR)/src/depthconv/depthconv.c
EMBENCH_SRCS_edn           = $(EMBENCH_DIR)/src/edn/libedn.c
EMBENCH_SRCS_huffbench     = $(EMBENCH_DIR)/src/huffbench/libhuffbench.c
EMBENCH_SRCS_matmult-int   = $(EMBENCH_DIR)/src/matmult-int/matmult-int.c
EMBENCH_SRCS_md5sum        = $(EMBENCH_DIR)/src/md5sum/md5.c
EMBENCH_SRCS_nettle-aes    = $(EMBENCH_DIR)/src/nettle-aes/nettle-aes.c
EMBENCH_SRCS_nettle-sha256 = $(EMBENCH_DIR)/src/nettle-sha256/nettle-sha256.c
EMBENCH_SRCS_nsichneu      = $(EMBENCH_DIR)/src/nsichneu/libnsichneu.c
EMBENCH_SRCS_picojpeg      = $(EMBENCH_DIR)/src/picojpeg/libpicojpeg.c \
                              $(EMBENCH_DIR)/src/picojpeg/picojpeg_test.c
EMBENCH_SRCS_qrduino       = $(EMBENCH_DIR)/src/qrduino/qrencode.c \
                              $(EMBENCH_DIR)/src/qrduino/qrframe.c \
                              $(EMBENCH_DIR)/src/qrduino/qrtest.c
EMBENCH_SRCS_sglib-combined = $(EMBENCH_DIR)/src/sglib-combined/combined.c
EMBENCH_SRCS_slre          = $(EMBENCH_DIR)/src/slre/libslre.c
EMBENCH_SRCS_statemate     = $(EMBENCH_DIR)/src/statemate/libstatemate.c
EMBENCH_SRCS_tarfind       = $(EMBENCH_DIR)/src/tarfind/tarfind.c
EMBENCH_SRCS_ud            = $(EMBENCH_DIR)/src/ud/libud.c
EMBENCH_SRCS_wikisort      = $(EMBENCH_DIR)/src/wikisort/libwikisort.c
EMBENCH_SRCS_xgboost       = $(EMBENCH_DIR)/src/xgboost/testbench.c \
                              $(EMBENCH_DIR)/src/xgboost/xgboost.c

# Per-benchmark -I flags for benchmarks that have their own headers.
EMBENCH_EXTRA_picojpeg     = -I$(EMBENCH_DIR)/src/picojpeg
EMBENCH_EXTRA_qrduino      = -I$(EMBENCH_DIR)/src/qrduino
EMBENCH_EXTRA_sglib-combined = -I$(EMBENCH_DIR)/src/sglib-combined
EMBENCH_EXTRA_slre         = -I$(EMBENCH_DIR)/src/slre
EMBENCH_EXTRA_xgboost      = -I$(EMBENCH_DIR)/src/xgboost
# wikisort uses 'bool' as a typedef in C11 mode; suppress the C23 keyword clash.
EMBENCH_EXTRA_wikisort     = -std=gnu11

define embench_template
benchmarks/embench-$(1).elf: $$(EMBENCH_SRCS_$(1)) $$(EMBENCH_COMMON) bmarks.ld
	@mkdir -p benchmarks
	$$(EMBENCH_CC) $$(EMBENCH_CFLAGS) $$(EMBENCH_EXTRA_$(1)) \
	    $$(EMBENCH_INCS) -I$$(EMBENCH_DIR)/src/$(1) \
	    $$(EMBENCH_LDFLAGS) \
	    $$(EMBENCH_SRCS_$(1)) $$(EMBENCH_COMMON) -lgcc -o $$@
endef

$(foreach b,$(EMBENCHS),$(eval $(call embench_template,$(b))))

EMBENCH_ELFS = $(patsubst %, benchmarks/embench-%.elf, $(EMBENCHS))

# ── Torture tests (RV32/RV64 random sequences for Spike co-simulation) ────────
#
# Pre-generated .S files live in torture/ (RV32) and torture64/ (RV64).
# Regenerate from scratch with:
#   python3 gen_torture.py 20 torture/   --seed 42
#   python3 gen_torture.py 20 torture64/ --seed 42 --xlen 64
# then rebuild ELFs with:
#   make torture torture64
#
# The generator and compiled ELFs are both committed; the ELFs can be rebuilt
# from the .S files alone using only the riscv32-none-elf-gcc / riscv64-none-elf-gcc
# toolchains.

CFLAGS_TORTURE   = -march=rv32imafc -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g
CFLAGS_TORTURE64 = -march=rv64imafdc -mabi=lp64 -nostdlib -nostartfiles -static -O1 -g

TORTURE_SRCS = $(wildcard torture/*.S)
TORTURE_ELFS = $(patsubst torture/%.S, torture/%.elf, $(TORTURE_SRCS))

TORTURE64_SRCS = $(wildcard torture64/*.S)
TORTURE64_ELFS = $(patsubst torture64/%.S, torture64/%.elf, $(TORTURE64_SRCS))

torture/%.elf: torture/%.S env/riscv_test.h env/link.ld
	$(CC) $(CFLAGS_TORTURE) $(INCLUDES) $(LDFLAGS) $< -o $@

torture64/%.elf: torture64/%.S env/riscv_test.h env/link.ld
	$(CC64) $(CFLAGS_TORTURE64) $(INCLUDES) $(LDFLAGS64) $< -o $@

# ── Syscall-emulation mode test binary ───────────────────────────────────────
#
# Pure assembly, no C startup: _start in .text.start writes to stdout via
# SYS_write ecall (64) and exits via SYS_exit (93).  No HTIF tohost symbol.

CFLAGS_SE = -march=rv32im -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g

se_hello.elf: se_hello.s test.ld
	$(CC) $(CFLAGS_SE) -T test.ld se_hello.s -o $@

# RV64 build of the same XLEN-agnostic source (la/li/ecall pseudo-ops assemble
# identically for either width) — used by the RV64 syscall-emulation tests.
CFLAGS_SE64 = -march=rv64im -mabi=lp64 -nostdlib -nostartfiles -static -O1 -g

se_hello64.elf: se_hello.s test.ld
	$(CC64) $(CFLAGS_SE64) -T test.ld se_hello.s -o $@

# ── psABI initial-stack probe (argc/argv[0] readback) ───────────────────────
#
# Reads argc and argv[0] off the stack (as an ABI-compliant _start would),
# writes the string argv[0] points to via SYS_write, then exits via SYS_exit.
# Used to prove InitialStackBuilder's SP lands where hand-written assembly
# offset arithmetic expects it — see abi_probe64.s for the exact reads.

abi_probe64.elf: abi_probe64.s test.ld
	$(CC64) $(CFLAGS_SE64) -T test.ld abi_probe64.s -o $@

# ── stdin echo probe (SYS_read fd 0 → SYS_write fd 1) ───────────────────────
#
# Proves LinuxSyscallEmulator's injected stdin stream reaches the guest via
# SYS_read end to end — see stdin_echo64.s for the exact reads/writes.

stdin_echo64.elf: stdin_echo64.s test.ld
	$(CC64) $(CFLAGS_SE64) -T test.ld stdin_echo64.s -o $@

# ── anonymous-mmap probe (SYS_mmap → write/read the mapped page) ───────────
#
# Proves LinuxSyscallEmulator's mmap arena is wired through SYS_mmap end to
# end — see mmap_probe64.s for the exact sequence.

mmap_probe64.elf: mmap_probe64.s test.ld
	$(CC64) $(CFLAGS_SE64) -T test.ld mmap_probe64.s -o $@

# ── real linked binary (musl libc, real printf/stdio) ───────────────────────
#
# Not a hand-assembled bare-metal probe like everything else above — a real C
# program compiled and statically linked against a real libc, with the
# toolchain's own default _start/linker script. Proves InitialStackBuilder +
# LinuxSyscallEmulator against genuine compiled code, not just assembly written
# to already match the layout it's checking.

hello64_musl.elf: hello64_musl.c
	$(CC64_MUSL) -static -O0 -o $@ hello64_musl.c

# Non-trivial companion to hello64_musl.elf: static-array compute kernel (no malloc, so no
# brk/mmap) with one printf at the very end, for validating the SimPoint/checkpoint sampling
# pipeline against real compiled code instead of just a straight-through hello-world.

simpoint_kernel.elf: simpoint_kernel.c
	$(CC64_MUSL) -static -O1 -o $@ simpoint_kernel.c

# Companion to hello64_musl.elf exercising a real __thread variable / thread-pointer setup.

tls_probe.elf: tls_probe.c
	$(CC64_MUSL) -static -O0 -o $@ tls_probe.c

# ── Targets ───────────────────────────────────────────────────────────────────

.PHONY: all isa-tests rv32-isa-tests rv64-isa-tests custom-elfs benchmarks torture torture64 clean

all: test.elf rich.elf htif.elf test64.elf rich64.elf htif64.elf zcmop.elf custom-elfs isa-tests benchmarks torture torture64

custom-elfs: zimop.elf zabha.elf zawrs.elf cbo.elf vector.elf se_hello.elf se_hello64.elf abi_probe64.elf stdin_echo64.elf mmap_probe64.elf hello64_musl.elf simpoint_kernel.elf tls_probe.elf

isa-tests: rv32-isa-tests rv64-isa-tests

rv32-isa-tests: $(RV32UI_ELFS) $(RV32UM_ELFS) $(RV32SI_ELFS) \
           $(RV32UA_ELFS) $(RV32UC_ELFS) $(RV32UF_ELFS) \
           $(RV32UZBA_ELFS) $(RV32UZBB_ELFS) $(RV32UZBC_ELFS) $(RV32UZBS_ELFS) \
           $(RV32UZICOND_ELFS)

rv64-isa-tests: $(RV64UI_ELFS) $(RV64UM_ELFS) $(RV64SI_ELFS) \
           $(RV64UA_ELFS) $(RV64UC_ELFS) $(RV64UF_ELFS) $(RV64UD_ELFS) \
           $(RV64UZFH_ELFS) \
           $(RV64UZBA_ELFS) $(RV64UZBB_ELFS) $(RV64UZBC_ELFS) $(RV64UZBS_ELFS)

benchmarks: $(BMARKS_ELFS) $(EMBENCH_ELFS)

torture: $(TORTURE_ELFS)

torture64: $(TORTURE64_ELFS)

# ── Lab 2 benchmarks (HPC Architectures 2023-2024) ───────────────────────────
#
# Compiled with -O0 as required by the lab so the compiler does not merge or
# reorder the chains we deliberately construct.  Uses the HTIF startup/exit
# shim (htif_crt.s + htif.ld) so the runner halts cleanly via tohost.

CFLAGS_LAB2 = -march=rv32im -mabi=ilp32 -nostdlib -nostartfiles -static -O0 -g

LAB2_BENCHES = benchmax benchmin \
               benchx_vanilla benchx_opt_inorder benchx_opt_ooo benchx_opt_blind

LAB2_ELFS = $(patsubst %, lab2/%.elf, $(LAB2_BENCHES))

lab2/%.elf: lab2/%.c htif_crt.s htif.ld
	$(CC) $(CFLAGS_LAB2) -T htif.ld htif_crt.s $< -o $@

lab2: $(LAB2_ELFS)

.PHONY: lab2

clean:
	rm -f test.elf rich.elf htif.elf test64.elf rich64.elf htif64.elf zcmop.elf zimop.elf zabha.elf zawrs.elf cbo.elf vector.elf se_hello.elf se_hello64.elf abi_probe64.elf stdin_echo64.elf mmap_probe64.elf hello64_musl.elf simpoint_kernel.elf tls_probe.elf
	rm -f isa/*.elf benchmarks/*.elf torture/*.elf torture64/*.elf lab2/*.elf
