# Minimal SE-mode test binary.
# Writes "Hello, SE mode!\n" to stdout via SYS_write (64), then exits
# via SYS_exit (93).  No HTIF, no ebreak — termination is through the
# LinuxSyscallEmulator returning RequestHalt on SYS_exit.

    .section .text.start, "ax"
    .global  _start
_start:
    la   a1, msg
    li   a2, 16         # len("Hello, SE mode!\n")
    li   a0, 1          # fd = stdout
    li   a7, 64         # SYS_write
    ecall
    li   a0, 0          # exit code
    li   a7, 93         # SYS_exit
    ecall
1:  j    1b

    .section .rodata
msg:
    .ascii "Hello, SE mode!\n"
