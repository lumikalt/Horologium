    .section .text.start, "ax"
    .global  _start
_start:
    li   sp, 0x80010000    # stack at top of 64 KB at DRAM_BASE
    call main
    ebreak
