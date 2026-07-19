// Non-trivial real-linked-binary fixture for validating the SimPoint/checkpoint sampling pipeline
// (Experiment.RunWithSimPointCheckpoints / --simpoint-warmup) against genuinely compiled code, not
// just the trivial entry/syscall/stdio path hello64_musl.elf proved. Static arrays (not malloc) keep
// the syscall surface to just musl's _start bookkeeping and the one final printf/exit_group — no
// brk/mmap in the hot loop, so the compute-dominated interior of the run is syscall-free and safe to
// checkpoint/restore into a fresh syscall-emulator instance per simulation point (see TODO.md/
// project_spec_harness.md for why full syscall-emulator-state checkpointing is out of scope here).
#include <stdio.h>

#define N 2048
#define ITERS 100

static int a[N];
static int b[N];

int main(void) {
    for (int i = 0; i < N; i++) {
        a[i] = i;
        b[i] = N - i;
    }

    long sum = 0;
    for (int iter = 0; iter < ITERS; iter++)
        for (int i = 0; i < N; i++) {
            a[i] = (a[i] * 3 + b[i]) % 1000003;
            sum += a[i];
        }

    printf("sum=%ld\n", sum);
    return 0;
}
