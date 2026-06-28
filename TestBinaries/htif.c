/*
 * RV32IM HTIF co-simulation fixture.
 *
 * Same workload character as rich.c (multiply/divide on runtime divisors, an
 * insertion sort with dependent store->load chains, heavy data-dependent
 * branching) but terminates through the HTIF 'tohost' register instead of
 * EBREAK, so standalone Spike exits cleanly rather than hanging in its debug
 * stub. htif_crt.s performs the tohost exit after main returns.
 *
 * tohost/fromhost live in the .tohost section (64-bit, per the HTIF ABI) so
 * Spike resolves them by symbol name. fromhost is never read here — there is
 * no printstr/syscall traffic — so the committed instruction stream stays a
 * clean prefix of Spike's, free of host-polling divergence.
 */

volatile unsigned long long tohost   __attribute__((section(".tohost")));
volatile unsigned long long fromhost __attribute__((section(".tohost")));

#define N 48

static unsigned lcg(unsigned *s) {
    *s = *s * 1664525u + 1013904223u;
    return *s;
}

int main(void) {
    int a[N];
    unsigned seed = 0x1234567u;

    for (int i = 0; i < N; i++) {
        unsigned r = lcg(&seed);
        unsigned d = ((r >> 8) & 0xFFu) + 1u;   /* runtime divisor → real remu */
        a[i] = (int)(r % d) - 128;
    }

    for (int i = 1; i < N; i++) {
        int key = a[i];
        int j = i - 1;
        while (j >= 0 && a[j] > key) {
            a[j + 1] = a[j];
            j--;
        }
        a[j + 1] = key;
    }

    long acc = 0;
    for (int i = 0; i < N; i++) {
        acc += (long)a[i] * (i + 1);
        int div = (a[i] & 7) + 1;               /* runtime divisor → real div */
        if (a[i] < 0) acc -= a[i] / div;
    }

    int ordered = 1;
    for (int i = 1; i < N; i++)
        if (a[i - 1] > a[i]) ordered = 0;

    /* Exit 0 on success so HTIF reports a clean pass; fold acc in so the
       compiler cannot elide the work. */
    return ordered && acc != 0 ? 0 : 1;
}
