// The first real, non-bare-metal Linux ELF fixture in this project: a genuinely linked binary
// (real musl libc, real _start, real printf/stdio) rather than a hand-assembled -nostdlib probe.
// Proves InitialStackBuilder + LinuxSyscallEmulator + the psABI entry convention work against an
// actual compiled C program, not just hand-written assembly that already assumes the layout it's
// checking. Built with riscv64-unknown-linux-musl-gcc -static (see TestBinaries/Makefile) — musl
// because it links statically with no extra flags, unlike the glibc cross toolchain (see flake.nix).
#include <stdio.h>

int main(int argc, char **argv) {
    printf("hello from a real linked binary, argv[0]=%s\n", argv[0]);
    return 0;
}
