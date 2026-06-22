/*
 * Bare-metal RV32I test program.
 *
 * Contract (checked by ElfLoaderTests.cs):
 *   x10 (a0) = 55   (sum 1..10, via loop in main)
 *   x11 (a1) = 13   (fib(7), via recursion, exercises JAL/JALR + stack)
 *   x12 (a2) = 255  (sum of bytes [1..16] stored/loaded via memory)
 */

static int fib(int n) {
    if (n <= 1) return n;
    return fib(n - 1) + fib(n - 2);
}

static int sum_array(const int *a, int len) {
    int s = 0;
    for (int i = 0; i < len; i++) s += a[i];
    return s;
}

int main(void) {
    /* Loop: sum 1 to 10 */
    int sum = 0;
    for (int i = 1; i <= 10; i++) sum += i;

    /* Recursion: fib(7) = 13 */
    int f = fib(7);

    /* Memory: store then load an array on the stack */
    int arr[16];
    for (int i = 0; i < 16; i++) arr[i] = i + 1;
    int arr_sum = sum_array(arr, 16);  /* 1+2+…+16 = 136 */

    register int r0 __asm__("a0") = sum;
    register int r1 __asm__("a1") = f;
    register int r2 __asm__("a2") = arr_sum;
    __asm__ volatile ("" : : "r"(r0), "r"(r1), "r"(r2));

    return sum;  /* also lands in a0 via normal ABI */
}
