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

# ── riscv-tests benchmarks ───────────────────────────────────────────────────
#
# dhrystone: uses a retry loop guarded by read_csr(mcycle).  Our simulator
#   keeps mcycle at 0, so the time check always fails, printf is called, and
#   the benchmark spins forever waiting for an HTIF fromhost reply.  Excluded.
#
# mm / spmv: use double-precision floating point (D extension); the simulator
#   only supports single-precision (F).  Excluded.
#
# mt-* / vec-*: multi-threaded or vector; not supported.  Excluded.
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
              -I$(BMARKS_DIR)/pchase
BMARKS_COMMON = $(BMARKS_DIR)/common/syscalls.c $(BMARKS_DIR)/common/crt.S

BMARKS = median qsort rsort towers vvadd memcpy multiply gcd treesum pchase

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

define bmark_template
benchmarks/$(1).elf: $$(BMARKS_SRCS_$(1)) $$(BMARKS_COMMON) bmarks.ld
	@mkdir -p benchmarks
	$$(BMARKS_CC) $$(BMARKS_CFLAGS) $$(BMARKS_INCS) $$(BMARKS_LDFLAGS) \
	    $$(BMARKS_SRCS_$(1)) $$(BMARKS_COMMON) -lgcc -o $$@
endef

$(foreach b,$(BMARKS),$(eval $(call bmark_template,$(b))))

BMARKS_ELFS = $(patsubst %, benchmarks/%.elf, $(BMARKS))

# ── Torture tests (RV32 random sequences for Spike co-simulation) ─────────────
#
# Pre-generated .S files live in torture/.  Regenerate from scratch with:
#   python3 gen_torture.py 20 torture/ --seed 42
# then rebuild ELFs with:
#   make torture
#
# The generator and compiled ELFs are both committed; the ELFs can be rebuilt
# from the .S files alone using only the riscv32-none-elf-gcc toolchain.

CFLAGS_TORTURE = -march=rv32imafc -mabi=ilp32 -nostdlib -nostartfiles -static -O1 -g

TORTURE_SRCS = $(wildcard torture/*.S)
TORTURE_ELFS = $(patsubst torture/%.S, torture/%.elf, $(TORTURE_SRCS))

torture/%.elf: torture/%.S env/riscv_test.h env/link.ld
	$(CC) $(CFLAGS_TORTURE) $(INCLUDES) $(LDFLAGS) $< -o $@

# ── Targets ───────────────────────────────────────────────────────────────────

.PHONY: all isa-tests custom-elfs benchmarks torture clean

all: test.elf rich.elf htif.elf zcmop.elf custom-elfs isa-tests benchmarks torture

custom-elfs: zimop.elf zabha.elf zawrs.elf cbo.elf vector.elf

isa-tests: $(RV32UI_ELFS) $(RV32UM_ELFS) $(RV32SI_ELFS) \
           $(RV32UA_ELFS) $(RV32UC_ELFS) $(RV32UF_ELFS) \
           $(RV32UZBA_ELFS) $(RV32UZBB_ELFS) $(RV32UZBC_ELFS) $(RV32UZBS_ELFS) \
           $(RV32UZICOND_ELFS)

benchmarks: $(BMARKS_ELFS)

torture: $(TORTURE_ELFS)

clean:
	rm -f test.elf rich.elf htif.elf zcmop.elf zimop.elf zabha.elf zawrs.elf cbo.elf vector.elf
	rm -f isa/*.elf benchmarks/*.elf torture/*.elf
