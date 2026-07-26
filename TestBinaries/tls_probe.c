// Companion to hello64_musl.elf: exercises thread-local storage in a real compiled binary. musl's
// own _start/__init_tls walks the ELF program header table (via AT_PHDR/AT_PHENT/AT_PHNUM) to find
// PT_TLS and set the thread pointer (tp, x4) with a plain register move — no syscall involved. A
// placeholder-zero AT_PHDR makes that walk dereference address 0 and crash on any binary declaring
// thread-local data, which this fixture caught (see InitialStackBuilder.BuildStandardAuxv's doc
// comment and Tests/RiscV64/System/RealLinkedBinaryTests.cs).
#include <stdio.h>

__thread int tls_var = 42;

static unsigned long read_tp(void) {
    unsigned long tp;
    __asm__ volatile("mv %0, tp" : "=r"(tp));
    return tp;
}

int main(void) {
    unsigned long tp = read_tp();
    printf("tp=%lx tls_var=%d &tls_var=%lx\n", tp, tls_var, (unsigned long)&tls_var);
    return 0;
}
