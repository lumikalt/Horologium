/* benchmin.c — minimise in-order vs OoO execution ratio
 * Single multiply chain with a loop-carried dependency: every iteration
 * depends on the previous one, so both pipelines must serialise. */
#include "htif.h"
static volatile int sink;
#define N 10000
int main(void) {
    int a = 1;
    for (int i = 0; i < N; i++)
        a = a * 3 + 1;
    sink = a;
    return 0;
}
