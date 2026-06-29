/*
 * RVV co-simulation workload.
 *
 * Enables VS (vector state) in mstatus so Spike does not trap on
 * vector instructions (Horologium ignores mstatus.VS but commits
 * the csrs identically, so both simulators agree).
 *
 * Tests: vsetvli, vmv.v.x (splat), vadd.vv, vmv.x.s (extract).
 * The vmv.x.s writes the first element (5 + 3 = 8) to an integer
 * register, which the co-sim verifies against Spike.
 */

int main(void) {
    /* mstatus.VS = Dirty (bits [10:9] = 11 = 0x600) */
    __asm__ volatile(
        "li   t0, 0x600\n\t"
        "csrs mstatus, t0"
        ::: "t0"
    );

    int result;
    __asm__ volatile(
        "vsetvli t0, zero, e32, m1, ta, ma\n\t"
        "li      t1, 5\n\t"
        "vmv.v.x v0, t1\n\t"       /* v0 = splat(5) */
        "li      t1, 3\n\t"
        "vmv.v.x v1, t1\n\t"       /* v1 = splat(3) */
        "vadd.vv v2, v0, v1\n\t"   /* v2 = 8 */
        "vmv.x.s %0, v2"           /* result = v2[0] = 8 */
        : "=r"(result)
        :
        : "t0", "t1"
    );
    return result;
}
