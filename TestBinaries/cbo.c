/*
 * Zicbom / Zicboz co-simulation workload.
 *
 * cbo.zero  zeroes a cache block (Zicboz).
 * cbo.inval invalidates a cache block without writing back (Zicbom).
 *
 * In Horologium's flat-memory model both are no-ops at the memory
 * level.  Co-sim verifies PC advance agreement with Spike.
 */

static char buf[64];

int main(void) {
    __asm__ volatile("cbo.zero  (%0)" : : "r"(buf) : "memory");
    __asm__ volatile("cbo.inval (%0)" : : "r"(buf) : "memory");
    return 0;
}
