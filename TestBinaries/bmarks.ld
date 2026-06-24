/* Linker script for riscv-tests benchmarks in Horologium.
 * Mirrors bmarks/common/test.ld but places everything at 0x0
 * so FlatMemory can hold it directly without a 2 GB allocation.
 * .tohost lands at 0x1000 (first page boundary after .text.init).
 */

OUTPUT_ARCH(riscv)
ENTRY(_start)

MEMORY {
    RAM (rwx) : ORIGIN = 0x00000000, LENGTH = 4M
}

SECTIONS {
    . = 0x0;
    .text.init : { *(.text.init) }

    . = ALIGN(0x1000);
    .tohost : { *(.tohost) }

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
