// Custom test environment for Horologium.
//
// The upstream env/p uses a tohost spin loop that relies on spike's
// HTIF interface.  Horologium halts on EBREAK, so we replace both
// RVTEST_PASS and RVTEST_FAIL with an EBREAK sequence.
//
// Pass/fail convention (matches the upstream riscv-tests semantic):
//   gp (x3) == 1           → PASS
//   gp (x3) == (N<<1)|1    → FAIL at sub-test N

#ifndef RISCV_TEST_H
#define RISCV_TEST_H

// TESTNUM is the register used to track which sub-test is running.
#define TESTNUM gp

// ISA-class markers used by some test preambles — no-ops in this env.
#define RVTEST_RV32U
#define RVTEST_RV64U
#define RVTEST_RV32UF
#define RVTEST_RV64UF

// Extra initialisation hook (empty — tests don't need CSR setup here).
#define EXTRA_INIT

// ---------------------------------------------------------------------------
// Code / data section boundaries
// ---------------------------------------------------------------------------

// Entry point.  Sets mstatus.FS=Dirty so Spike (which enforces the
// mstatus.FS field) does not trap on FP instructions.  Horologium
// ignores mstatus.FS for FP execution but commits the csrs instruction
// identically, so both simulators agree instruction-for-instruction.
// Stack pointer follows.
#define RVTEST_CODE_BEGIN   \
    .section .text.start;   \
    .global  _start;        \
_start:                     \
    li   t0, 0x6000;        \
    csrs mstatus, t0;       \
    li   sp, 0xF000;

#define RVTEST_CODE_END

#define RVTEST_DATA_BEGIN   \
    .section .data;         \
    .align 2

#define RVTEST_DATA_END

// ---------------------------------------------------------------------------
// Pass / fail termination
// ---------------------------------------------------------------------------

// RVTEST_PASS: set gp = 1, then halt.
#define RVTEST_PASS         \
    li   gp, 1;             \
    ebreak

// RVTEST_FAIL: gp already holds the failing sub-test number.
// Encode it as (N << 1) | 1 so it is non-zero and distinguishable from
// PASS (gp == 1), then halt.
#define RVTEST_FAIL         \
    fence;                  \
    slli gp, gp, 1;         \
    ori  gp, gp, 1;         \
    ebreak

#endif /* RISCV_TEST_H */
