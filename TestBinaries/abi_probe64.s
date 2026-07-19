# psABI initial-stack probe (RV64).
#
# Proves InitialStackBuilder's SP lands exactly where a real _start would
# expect it, using hand-written offset arithmetic independent of the C#
# builder: reads argc from (sp), argv[0]'s pointer from 8(sp) (rv64 word
# size), computes its length with a manual strlen loop, writes it via
# SYS_write, then exits via SYS_exit. No HTIF, no ebreak.

    .section .text.start, "ax"
    .global  _start
_start:
    ld   a1, 8(sp)      # argv[0] pointer
    mv   a2, a1
1:  lb   t0, 0(a2)
    beqz t0, 2f
    addi a2, a2, 1
    j    1b
2:  sub  a2, a2, a1      # a2 = strlen(argv[0])
    li   a0, 1           # fd = stdout
    li   a7, 64          # SYS_write
    ecall
    li   a0, 0           # exit code
    li   a7, 93          # SYS_exit
    ecall
3:  j    3b
