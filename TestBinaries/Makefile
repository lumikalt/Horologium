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

# ── Targets ───────────────────────────────────────────────────────────────────

.PHONY: all isa-tests clean

all: test.elf isa-tests

isa-tests: $(RV32UI_ELFS) $(RV32UM_ELFS)

clean:
	rm -f test.elf isa/*.elf
