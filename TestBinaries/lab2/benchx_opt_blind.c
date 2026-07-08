/* benchx_opt_blind.c — "blind" optimisation: 2 accumulators with 2x
 * unroll.  A reasonable programmer's guess that helps both pipelines
 * somewhat but is not tuned specifically to either. */
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
    int s0 = 0, s1 = 0;
    for (int r = 0; r < REPS; r++) {
        int i = 0;
        for (; i < N - 1; i += 2) {
            s0 += A[i+0] * A[i+0];
            s1 += A[i+1] * A[i+1];
        }
        for (; i < N; i++)
            s0 += A[i] * A[i];
    }
    sink = s0 + s1;
    return 0;
}
