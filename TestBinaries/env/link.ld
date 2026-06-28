/* Linker script for Horologium riscv-tests.
 * Uses Spike's DRAM_BASE (0x80000000) for lock-step co-simulation compatibility.
 * 64 KB is enough for every individual instruction test.
 */

OUTPUT_ARCH(riscv)
ENTRY(_start)

MEMORY {
    RAM (rwx) : ORIGIN = 0x80000000, LENGTH = 64K
}

SECTIONS {
    . = 0x80000000;

    .text.start : { *(.text.start) } > RAM
    .text       : { *(.text .text.*)
                    *(.rodata .rodata.*) } > RAM

    .data       : { *(.data .data.*)
                    *(.sdata .sdata.*) } > RAM

    .bss        : { *(.bss .bss.*)
                    *(.sbss .sbss.*)
                    *(COMMON) } > RAM
}
