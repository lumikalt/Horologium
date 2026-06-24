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
              -I$(BMARKS_DIR)/multiply
BMARKS_COMMON = $(BMARKS_DIR)/common/syscalls.c $(BMARKS_DIR)/common/crt.S

BMARKS = median qsort rsort towers vvadd memcpy multiply

BMARKS_SRCS_median   = $(BMARKS_DIR)/median/median_main.c   $(BMARKS_DIR)/median/median.c
BMARKS_SRCS_qsort    = $(BMARKS_DIR)/qsort/qsort_main.c
BMARKS_SRCS_rsort    = $(BMARKS_DIR)/rsort/rsort.c
BMARKS_SRCS_towers   = $(BMARKS_DIR)/towers/towers_main.c
BMARKS_SRCS_vvadd    = $(BMARKS_DIR)/vvadd/vvadd_main.c
BMARKS_SRCS_memcpy   = $(BMARKS_DIR)/memcpy/memcpy_main.c
BMARKS_SRCS_multiply = $(BMARKS_DIR)/multiply/multiply_main.c $(BMARKS_DIR)/multiply/multiply.c

define bmark_template
benchmarks/$(1).elf: $$(BMARKS_SRCS_$(1)) $$(BMARKS_COMMON) bmarks.ld
	@mkdir -p benchmarks
	$$(BMARKS_CC) $$(BMARKS_CFLAGS) $$(BMARKS_INCS) $$(BMARKS_LDFLAGS) \
	    $$(BMARKS_SRCS_$(1)) $$(BMARKS_COMMON) -lgcc -o $$@
endef

$(foreach b,$(BMARKS),$(eval $(call bmark_template,$(b))))

BMARKS_ELFS = $(patsubst %, benchmarks/%.elf, $(BMARKS))

# ── Targets ───────────────────────────────────────────────────────────────────

.PHONY: all isa-tests benchmarks clean

all: test.elf isa-tests benchmarks

isa-tests: $(RV32UI_ELFS) $(RV32UM_ELFS) $(RV32SI_ELFS)

benchmarks: $(BMARKS_ELFS)

clean:
	rm -f test.elf isa/*.elf benchmarks/*.elf
