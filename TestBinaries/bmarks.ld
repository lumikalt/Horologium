/* Linker script for riscv-tests benchmarks in Horologium.
 * Uses Spike's DRAM_BASE (0x80000000) as the load address so that ELFs can
 * be run unmodified under spike for lock-step co-simulation.
 * .tohost lands at 0x80001000 (first page boundary after .text.init).
 */

OUTPUT_ARCH(riscv)
ENTRY(_start)

MEMORY {
    RAM (rwx) : ORIGIN = 0x80000000, LENGTH = 4M
}

SECTIONS {
    . = 0x80000000;
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
