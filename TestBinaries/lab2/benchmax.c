/* benchmax.c — maximise in-order vs OoO execution ratio
 * Four independent multiply chains: OoO overlaps them; in-order serialises.
 * Compile with -O0 so the compiler does not merge the chains. */
#include "htif.h"
static volatile int sink;
#define N 3000
int main(void) {
    int a = 1, b = 2, c = 3, d = 4;
    for (int i = 0; i < N; i++) {
        a = a * 3 + 1;
        b = b * 5 + 2;
        c = c * 7 + 3;
        d = d * 11 + 5;
    }
    sink = a ^ b ^ c ^ d;
    return 0;
}
