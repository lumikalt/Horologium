/* Linker script for Horologium riscv-tests.
 * Places everything at address 0x0 so FlatMemory can hold it directly.
 * 64 KB is enough for every individual instruction test.
 */

OUTPUT_ARCH(riscv)
ENTRY(_start)

MEMORY {
    RAM (rwx) : ORIGIN = 0x00000000, LENGTH = 64K
}

SECTIONS {
    . = 0x0;

    .text.start : { *(.text.start) } > RAM
    .text       : { *(.text .text.*)
                    *(.rodata .rodata.*) } > RAM

    .data       : { *(.data .data.*)
                    *(.sdata .sdata.*) } > RAM

    .bss        : { *(.bss .bss.*)
                    *(.sbss .sbss.*)
                    *(COMMON) } > RAM
}
