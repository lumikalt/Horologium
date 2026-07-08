// Minimal smoke-test binary for the gem5 SE O3CPU pipeline.
// Runs a short integer loop and exits 0.  No HTIF, no M-mode CSRs.

extern void setStats(int);

int main(void) {
    setStats(1);
    volatile int sum = 0;
    for (int i = 0; i < 1000; i++)
        sum += i;
    setStats(0);
    return sum == 499500 ? 0 : 1;
}
