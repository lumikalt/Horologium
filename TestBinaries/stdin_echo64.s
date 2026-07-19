# stdin echo probe (RV64).
#
# Proves LinuxSyscallEmulator's injected stdin stream is wired through SYS_read
# end to end: reads up to 64 bytes from fd 0, writes exactly the bytes read
# (SYS_read's return value, not the fixed buffer size) to fd 1, then exits.
# No HTIF, no ebreak — same bare-metal SE-mode convention as abi_probe64.s.

    .section .bss
    .align 3
buf:
    .space 64

    .section .text.start, "ax"
    .global  _start
_start:
    li   a0, 0          # fd = stdin
    la   a1, buf
    li   a2, 64         # count
    li   a7, 63         # SYS_read
    ecall
    mv   a2, a0         # a2 = bytes actually read
    li   a0, 1          # fd = stdout
    la   a1, buf
    li   a7, 64         # SYS_write
    ecall
    li   a0, 0          # exit code
    li   a7, 93         # SYS_exit
    ecall
1:  j    1b
