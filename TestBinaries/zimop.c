/*
 * Zimop co-simulation workload.
 *
 * Exercises mop.r.N and mop.rr.N (Zimop hint NOPs) using raw .4byte
 * encodings.  Both write 0 to rd (x10/a0) and are otherwise no-ops.
 * Co-sim verifies PC advance and that rd is set to 0 for each.
 *
 * Encodings (from the Zimop spec):
 *   mop.r.0  rd=a0 : 0x81C04573
 *   mop.rr.0 rd=a0, rs1=a1 : 0x8235C573
 */

int main(void) {
    /* mop.r.0: rd = x10 (a0), always writes 0 */
    __asm__ volatile(".4byte 0x81C04573" ::: "a0");
    /* mop.rr.0: rd = x10 (a0), rs1 = x11 (a1), always writes 0 */
    __asm__ volatile(".4byte 0x8235C573" ::: "a0");
    return 0;
}
