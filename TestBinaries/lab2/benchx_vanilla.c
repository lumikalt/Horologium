/* benchx_vanilla.c — sum of squares, single accumulator, no unrolling.
 * Baseline for Activity 2. */
#include "htif.h"
static volatile int sink;
#define REPS 30
#define N    256
static int A[N];

static void init(void) {
    unsigned x = 1013904223u;
    for (int i = 0; i < N; i++) { x = x * 1664525u + 1013904223u; A[i] = (int)(x >> 16) & 0x7f; }
}

int main(void) {
    init();
    int sum = 0;
    for (int r = 0; r < REPS; r++)
        for (int i = 0; i < N; i++)
            sum += A[i] * A[i];
    sink = sum;
    return 0;
}
