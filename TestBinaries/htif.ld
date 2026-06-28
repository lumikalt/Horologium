/* Linker script for the HTIF co-sim fixture.
 * Load address is Spike's DRAM_BASE (0x80000000) for co-simulation.
 * .tohost lands on a page boundary so Spike resolves the tohost/fromhost
 * HTIF symbols, just like bmarks.ld. 1 MB region leaves room for the stack.
 */

OUTPUT_ARCH(riscv)
ENTRY(_start)

MEMORY {
    RAM (rwx) : ORIGIN = 0x80000000, LENGTH = 1M
}

SECTIONS {
    . = 0x80000000;

    .text.start : { *(.text.start) } > RAM

    . = ALIGN(0x1000);
    .tohost     : { *(.tohost) } > RAM

    . = ALIGN(0x1000);
    .text       : { *(.text .text.*)
                    *(.rodata .rodata.*) } > RAM

    .data       : { *(.data .data.*)
                    *(.sdata .sdata.*) } > RAM

    .bss        : { *(.bss .bss.*)
                    *(.sbss .sbss.*)
                    *(COMMON) } > RAM
}
