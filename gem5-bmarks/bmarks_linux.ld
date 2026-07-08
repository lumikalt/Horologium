/* Linker script for Linux SE mode gem5 benchmarks.
 * Load at 0x10000 (user-space; no HTIF, no Spike DRAM_BASE).
 * Stack space is provided by gem5's SE process loader.
 */

OUTPUT_ARCH(riscv)
ENTRY(_start)

MEMORY {
    RAM (rwx) : ORIGIN = 0x10000, LENGTH = 8M
}

SECTIONS {
    . = 0x10000;
    .text.init : { *(.text.init) }

    . = ALIGN(0x1000);
    .text : { *(.text .text.*) }

    .rodata : { *(.rodata .rodata.*) }

    .data : { *(.data .data.*) }

    .sdata : {
        __global_pointer$ = . + 0x800;
        *(.srodata .srodata.*)
        *(.sdata .sdata.* .gnu.linkonce.s.*)
    }

    .sbss : {
        *(.sbss .sbss.* .gnu.linkonce.sb.*)
        *(.scommon)
    }

    .bss : { *(.bss .bss.*) *(COMMON) }

    .tdata : { _tdata_begin = .; *(.tdata) _tdata_end = .; }
    .tbss  : { *(.tbss) _tbss_end = .; }

    _end = .;
}
