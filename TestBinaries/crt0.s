    .section .text.start, "ax"
    .global  _start
_start:
    li   sp, 0x10000    # stack at top of 64 KB
    call main
    ebreak
