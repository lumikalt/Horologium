# anonymous-mmap probe (RV64).
#
# Proves LinuxSyscallEmulator's mmap arena is wired through SYS_mmap end to end:
# maps one anonymous page, writes a byte into it, reads it back (proving the
# mapped region is genuinely read/write memory, not just a returned address),
# then writes that byte to stdout via SYS_write and exits. No HTIF, no ebreak.

    .section .text.start, "ax"
    .global  _start
_start:
    li   a0, 0            # addr hint = 0 (ignored, no MAP_FIXED)
    li   a1, 4096         # length
    li   a2, 3            # prot (ignored)
    li   a3, 0x22         # flags = MAP_PRIVATE(0x02) | MAP_ANONYMOUS(0x20)
    li   a4, -1           # fd = -1 (conventional for anonymous mappings)
    li   a5, 0            # offset
    li   a7, 222          # SYS_mmap
    ecall
    mv   a1, a0           # a1 = mapped address (SYS_mmap's return value)

    li   t0, 0x2A         # '*'
    sb   t0, 0(a1)        # write into the mapped region
    lb   a2, 0(a1)        # read it back — proves it's real R/W memory, not a stub

    li   a0, 1            # fd = stdout
    li   a2, 1            # count = 1
    li   a7, 64           # SYS_write (writes the byte at a1, still '*')
    ecall

    li   a0, 0            # exit code
    li   a7, 93           # SYS_exit
    ecall
1:  j    1b
