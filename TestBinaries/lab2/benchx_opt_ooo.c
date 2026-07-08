/* benchx_opt_ooo.c — optimised for OoO: 4 independent accumulators
 * break the add-chain dependency so the OoO engine can issue multiple
 * multiply-accumulate chains in parallel. */
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
    int s0 = 0, s1 = 0, s2 = 0, s3 = 0;
    for (int r = 0; r < REPS; r++) {
        int i = 0;
        for (; i < N - 3; i += 4) {
            s0 += A[i+0] * A[i+0];
            s1 += A[i+1] * A[i+1];
            s2 += A[i+2] * A[i+2];
            s3 += A[i+3] * A[i+3];
        }
        for (; i < N; i++)
            s0 += A[i] * A[i];
    }
    sink = s0 + s1 + s2 + s3;
    return 0;
}
