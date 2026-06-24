// Custom supervisor test environment for Horologium (rv32si-p-* tests).
//
// All supervisor tests run in machine-mode masquerade (__MACHINE_MODE):
// each test's internal #ifdef __MACHINE_MODE blocks alias supervisor CSR
// names to their machine-mode equivalents so no actual S-mode hardware is
// needed.
//
// Trap handler contract:
//   RVTEST_CODE_BEGIN writes mtvec = mtvec_handler.
//   Tests that need a trap handler export .global mtvec_handler.
//   Tests that do not need one fall back to default_trap.S (weak symbol).
//
// Pass/fail convention (same as rv32ui env):
//   gp (x3) == 1           → PASS
//   gp (x3) == (N<<1)|1    → FAIL at sub-test N

#ifndef RISCV_TEST_S_H
#define RISCV_TEST_S_H

// ── Trap cause codes ──────────────────────────────────────────────────────────

#define CAUSE_INSTRUCTION_MISALIGNED  0
#define CAUSE_INSTRUCTION_ACCESS      1
#define CAUSE_ILLEGAL_INSTRUCTION     2
#define CAUSE_BREAKPOINT              3
#define CAUSE_LOAD_MISALIGNED         4
#define CAUSE_LOAD_ACCESS             5
#define CAUSE_STORE_MISALIGNED        6
#define CAUSE_STORE_ACCESS            7
#define CAUSE_USER_ECALL              8
#define CAUSE_SUPERVISOR_ECALL        9
#define CAUSE_MACHINE_ECALL           11
#define CAUSE_INSTRUCTION_PAGE_FAULT  12
#define CAUSE_LOAD_PAGE_FAULT         13
#define CAUSE_STORE_PAGE_FAULT        15

// ── Status and interrupt field masks ─────────────────────────────────────────

// sstatus bit positions (same as mstatus for the S-visible fields)
#define SSTATUS_UIE  0x00000001UL
#define SSTATUS_SIE  0x00000002UL
#define SSTATUS_UPIE 0x00000010UL
#define SSTATUS_SPIE 0x00000020UL
#define SSTATUS_SPP  0x00000100UL
#define SSTATUS_UXL  0UL          // not present in RV32

// mstatus bit positions used by tests compiled with __MACHINE_MODE
#define MSTATUS_SIE  0x00000002UL
#define MSTATUS_MIE  0x00000008UL
#define MSTATUS_SPIE 0x00000020UL
#define MSTATUS_MPIE 0x00000080UL
#define MSTATUS_SPP  0x00000100UL
#define MSTATUS_MPP  0x00001800UL
#define MSTATUS_FS   0x00006000UL
#define MSTATUS_MPRV 0x00020000UL
#define MSTATUS_SUM  0x00040000UL

// Supervisor interrupt bits in sip/sie
#define SIP_SSIP 0x00000002UL    // supervisor software interrupt pending/enable
#define SIP_STIP 0x00000020UL    // supervisor timer interrupt
#define SIP_SEIP 0x00000200UL    // supervisor external interrupt

// Privilege level constants
#define PRV_U  0
#define PRV_S  1
#define PRV_M  3

// ── Machine-mode masquerade ───────────────────────────────────────────────────
// Defining __MACHINE_MODE causes each test's internal #ifdef blocks to alias
// all supervisor CSR accesses to the equivalent machine-mode CSRs.
#define __MACHINE_MODE

// ── Test framework macros ─────────────────────────────────────────────────────

#define TESTNUM gp

// ISA class markers — no-ops for bare-metal (-p) tests.
#define RVTEST_RV32S
#define RVTEST_RV64S
#define RVTEST_RV32M
#define RVTEST_RV64M

// Entry point: set up stack pointer and wire mtvec to the test's trap handler.
#define RVTEST_CODE_BEGIN               \
    .section .text.start;               \
    .global  _start;                    \
_start:                                 \
    li   sp, 0xF000;                    \
    la   t0, mtvec_handler;             \
    csrw mtvec, t0;

#define RVTEST_CODE_END

#define RVTEST_DATA_BEGIN   \
    .section .data;         \
    .align 2

#define RVTEST_DATA_END

// Pass/fail signaling (same scheme as the rv32ui env).
#define RVTEST_PASS         \
    li   gp, 1;             \
    ebreak

#define RVTEST_FAIL         \
    fence;                  \
    slli gp, gp, 1;         \
    ori  gp, gp, 1;         \
    ebreak

// ── EXTRA_INIT ────────────────────────────────────────────────────────────────
// Some tests call EXTRA_INIT as an optional setup hook (e.g. rv32si/csr.S).
#define EXTRA_INIT

#endif /* RISCV_TEST_S_H */
