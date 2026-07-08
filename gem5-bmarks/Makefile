# Build Linux SE mode benchmark variants for gem5 O3CPU comparison.
#
# These ELFs use Linux syscalls (ecall exit) rather than HTIF tohost writes,
# so gem5 SE mode can load them directly without a FS setup.
#
# Usage:
#   make hello.elf          # minimal smoke-test (de-risk gem5 pipe first)
#   make benchmarks         # all 10 riscv-tests benchmark variants
#
# All ELFs land in the current directory (gem5-bmarks/).

BMARKS_DIR = ../TestBinaries/riscv-tests/benchmarks

CC     = riscv32-none-elf-gcc
CFLAGS = -march=rv32imafd_zicsr -mabi=ilp32d -O2 -static \
         -nostdlib -nostartfiles \
         -DPREALLOCATE=1 -mcmodel=medany -fno-common \
         -fno-builtin-printf -fno-tree-loop-distribute-patterns \
         -Wno-implicit-int -Wno-implicit-function-declaration
LDFLAGS = -T bmarks_linux.ld

INCS = -I$(BMARKS_DIR) -I$(BMARKS_DIR)/common \
       -I../TestBinaries/riscv-tests/env \
       -I$(BMARKS_DIR)/median  -I$(BMARKS_DIR)/qsort \
       -I$(BMARKS_DIR)/rsort   -I$(BMARKS_DIR)/towers \
       -I$(BMARKS_DIR)/vvadd   -I$(BMARKS_DIR)/memcpy \
       -I$(BMARKS_DIR)/multiply -I$(BMARKS_DIR)/gcd \
       -I$(BMARKS_DIR)/treesum -I$(BMARKS_DIR)/pchase

COMMON = crt_linux.S syscalls_linux.c

# ── Minimal smoke-test binary ─────────────────────────────────────────────────

HELLO_SRC = hello_main.c

hello.elf: $(HELLO_SRC) $(COMMON) bmarks_linux.ld
	$(CC) $(CFLAGS) $(LDFLAGS) $(COMMON) $(HELLO_SRC) -lgcc -o $@

# ── riscv-tests benchmark variants ───────────────────────────────────────────

BMARKS = median qsort rsort towers vvadd memcpy multiply gcd treesum pchase

SRCS_median   = $(BMARKS_DIR)/median/median_main.c   $(BMARKS_DIR)/median/median.c
SRCS_qsort    = $(BMARKS_DIR)/qsort/qsort_main.c
SRCS_rsort    = $(BMARKS_DIR)/rsort/rsort.c
SRCS_towers   = $(BMARKS_DIR)/towers/towers_main.c
SRCS_vvadd    = $(BMARKS_DIR)/vvadd/vvadd_main.c
SRCS_memcpy   = $(BMARKS_DIR)/memcpy/memcpy_main.c
SRCS_multiply = $(BMARKS_DIR)/multiply/multiply_main.c $(BMARKS_DIR)/multiply/multiply.c
SRCS_gcd      = $(BMARKS_DIR)/gcd/gcd_main.c
SRCS_treesum  = $(BMARKS_DIR)/treesum/treesum_main.c
SRCS_pchase   = $(BMARKS_DIR)/pchase/pchase_main.c

define bmark_template
$(1).elf: $$(SRCS_$(1)) $$(COMMON) bmarks_linux.ld
	$$(CC) $$(CFLAGS) $$(INCS) $$(LDFLAGS) $$(COMMON) $$(SRCS_$(1)) -lgcc -o $$@
endef

$(foreach b,$(BMARKS),$(eval $(call bmark_template,$(b))))

.PHONY: benchmarks clean
benchmarks: $(addsuffix .elf, $(BMARKS))

clean:
	rm -f hello.elf $(addsuffix .elf, $(BMARKS))
