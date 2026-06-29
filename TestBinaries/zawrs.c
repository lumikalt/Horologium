/*
 * Zawrs co-simulation workload.
 *
 * wrs.nto and wrs.sto are "wait for reservation set" hints.  In a
 * single-threaded simulator they are no-ops that do not write any
 * register.  Co-sim verifies that both Spike and Horologium advance
 * PC past them without divergence.
 */

int main(void) {
    __asm__ volatile("wrs.nto");
    __asm__ volatile("wrs.sto");
    return 0;
}
