/* benchx_opt_inorder.c — optimised for in-order: 4x manual unroll to
 * reduce loop-control overhead while keeping a single accumulator
 * (OoO gains little from this since the add chain is still serial). */
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
    for (int r = 0; r < REPS; r++) {
        int i = 0;
        for (; i < N - 3; i += 4) {
            sum += A[i+0] * A[i+0];
            sum += A[i+1] * A[i+1];
            sum += A[i+2] * A[i+2];
            sum += A[i+3] * A[i+3];
        }
        for (; i < N; i++)
            sum += A[i] * A[i];
    }
    sink = sum;
    return 0;
}
