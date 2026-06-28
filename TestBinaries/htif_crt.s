# Minimal HTIF startup for the co-sim fixture.
#
# Calls main, then signals exit through the HTIF 'tohost' register the same way
# riscv-tests does: write (code << 1) | 1 (always odd → exit, not a syscall),
# then spin. Spike's HTIF processes the tohost write asynchronously and exits;
# Horologium's self-loop halt (or, for OoO, the jump-to-self detection added
# alongside this fixture) stops right after the store commits. No printstr, so
# there is no fromhost polling to diverge against Spike.
    .section .text.start, "ax"
    .global  _start
_start:
    li   sp, 0x80040000        # stack top, well within the 1 MB region
    call main
    slli a0, a0, 1             # tohost = (exit_code << 1) | 1
    ori  a0, a0, 1
    la   t0, tohost
    sw   a0, 0(t0)
1:  j    1b
