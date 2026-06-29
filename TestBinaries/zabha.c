/*
 * Zabha co-simulation workload.
 *
 * Exercises byte and halfword AMO instructions (Zabha extension):
 *   amoswap.b / amoadd.b — byte-width atomics
 *   amoswap.h / amoadd.h — halfword-width atomics
 *
 * Initial values are non-zero so the old-value returns are interesting.
 * Co-sim verifies the integer destination register after each AMO.
 */

static volatile unsigned char  g_byte = 7;
static volatile unsigned short g_half = 1000;

int main(void) {
    int r;

    /* amoswap.b: exchange g_byte (7) with 42; r = sign-extended old = 7 */
    __asm__ volatile("amoswap.b %0, %2, (%1)"
        : "=r"(r) : "r"(&g_byte), "r"(42) : "memory");

    /* amoadd.b: g_byte (42) += 10 = 52; r = sign-extended old = 42 */
    __asm__ volatile("amoadd.b %0, %2, (%1)"
        : "=r"(r) : "r"(&g_byte), "r"(10) : "memory");

    /* amoswap.h: exchange g_half (1000) with 777; r = sign-extended old = 1000 */
    __asm__ volatile("amoswap.h %0, %2, (%1)"
        : "=r"(r) : "r"(&g_half), "r"(777) : "memory");

    /* amoadd.h: g_half (777) += 100 = 877; r = sign-extended old = 777 */
    __asm__ volatile("amoadd.h %0, %2, (%1)"
        : "=r"(r) : "r"(&g_half), "r"(100) : "memory");

    return r;
}
