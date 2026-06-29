/*
 * Bare-metal Zcmop co-sim workload.
 *
 * Exercises all 8 c.mop.N variants (N odd, 1..15) via raw .2byte directives.
 * Each is a hint NOP: registers and memory must be unchanged after each one.
 *
 * Contract (checked by Spike co-sim):
 *   a0 = 0xAB  (sentinel placed before the mops, unchanged after)
 *   a1 = 0xCD  (second sentinel)
 */

int main(void) {
    register int r0 __asm__("a0") = 0xAB;
    register int r1 __asm__("a1") = 0xCD;

    /* Emit all 8 c.mop.N as 16-bit literals in the instruction stream.
     * Encodings from Spike's encoding.h: (c & 0xF8FF) == 0x6081,
     * bits[10:8] = N_index, N = 2*N_index+1 ∈ {1,3,5,...,15}. */
    __asm__ volatile (
        ".2byte 0x6081\n\t"   /* c.mop.1  */
        ".2byte 0x6181\n\t"   /* c.mop.3  */
        ".2byte 0x6281\n\t"   /* c.mop.5  */
        ".2byte 0x6381\n\t"   /* c.mop.7  */
        ".2byte 0x6481\n\t"   /* c.mop.9  */
        ".2byte 0x6581\n\t"   /* c.mop.11 */
        ".2byte 0x6681\n\t"   /* c.mop.13 */
        ".2byte 0x6781\n\t"   /* c.mop.15 */
        : "+r"(r0), "+r"(r1)
    );

    return r0;
}
