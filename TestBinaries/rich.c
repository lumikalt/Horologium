/*
 * Richer bare-metal RV32IM co-simulation workload.
 *
 * Deliberately exercises the datapath that the in-order (FiveStage) pipeline
 * and the out-of-order (Oooe) core newly co-simulate against Spike, beyond
 * what test.c's straight-line loops reach:
 *
 *   - M extension: multiply / unsigned-remainder / signed-divide land on the
 *     multi-cycle functional units, exercising the OoO in-flight countdown and
 *     issue-queue wakeup paths.
 *   - Insertion sort: long runs of dependent store->load sequences, hitting
 *     store-to-load forwarding and speculative memory disambiguation.
 *   - Data-dependent branches everywhere (sort comparisons, sign tests, the
 *     monotonic check) to drive branch mispredictions and pipeline flushes.
 *
 * No HTIF: control returns to crt0, which issues EBREAK to halt. Spike is the
 * oracle, so nothing is hard-coded here; the co-sim asserts commit-for-commit
 * agreement. crt0 does not clear .bss, so every byte read is written first
 * (the array lives on the stack and is fully filled before any read).
 */

#define N 48

/* Linear-congruential PRNG: one multiply per step. */
static unsigned lcg(unsigned *s) {
    *s = *s * 1664525u + 1013904223u;
    return *s;
}

int main(void) {
    int a[N];
    unsigned seed = 0x1234567u;

    /* Fill with pseudo-random signed values. The divisor is a runtime value
       (built with shift/mask/add, never zero) so gcc emits real divu/remu
       rather than strength-reducing a constant divisor into a multiply. */
    for (int i = 0; i < N; i++) {
        unsigned r = lcg(&seed);
        unsigned d = ((r >> 8) & 0xFFu) + 1u;   /* 1..256, no division */
        a[i] = (int)(r % d) - 128;              /* remu on the M unit */
    }

    /* Insertion sort: dependent load/store chains + data-dependent branches. */
    for (int i = 1; i < N; i++) {
        int key = a[i];
        int j = i - 1;
        while (j >= 0 && a[j] > key) {
            a[j + 1] = a[j];   /* stored this iter, re-loaded the next */
            j--;
        }
        a[j + 1] = key;
    }

    /* Checksum the sorted array, mixing index products (mul) and a signed
       divide by a runtime, never-zero divisor (real div on the M unit). */
    long acc = 0;
    for (int i = 0; i < N; i++) {
        acc += (long)a[i] * (i + 1);
        int div = (a[i] & 7) + 1;   /* 1..8, runtime */
        if (a[i] < 0) acc -= a[i] / div;
    }

    /* Confirm monotonic ordering (a chain of data-dependent branches). */
    int ordered = 1;
    for (int i = 1; i < N; i++)
        if (a[i - 1] > a[i]) ordered = 0;

    register int r0 __asm__("a0") = (int)acc;
    register int r1 __asm__("a1") = ordered;
    __asm__ volatile ("" : : "r"(r0), "r"(r1));
    return ordered;
}
