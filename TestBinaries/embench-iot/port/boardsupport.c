/* Horologium bare-metal BSP for Embench-IoT.
 *
 * Runs under the riscv-tests crt.S + syscalls.c environment:
 *   - main() returns 0 on pass, 1 on fail
 *   - crt.S routes that return value to tohost_exit(code)
 *   - tohost low word == 1 → exit(0) → PASS in BenchmarkTests
 *
 * start_trigger / stop_trigger are no-ops: BenchmarkTests reads
 * cycle counts from the DialBoard snapshot, not from anything the
 * benchmark self-reports.
 */

#include "support.h"

void
initialise_board (void)
{
}

void
start_trigger (void)
{
}

void
stop_trigger (void)
{
}

/* memcmp — not provided by riscv-tests syscalls.c */
int
memcmp (const void *s1, const void *s2, size_t n)
{
  const unsigned char *a = (const unsigned char *)s1;
  const unsigned char *b = (const unsigned char *)s2;
  for (size_t i = 0; i < n; i++)
    {
      if (a[i] < b[i]) return -1;
      if (a[i] > b[i]) return  1;
    }
  return 0;
}

/* strchr — needed by slre */
char *
strchr (const char *s, int c)
{
  for (; *s != '\0'; s++)
    if ((unsigned char)*s == (unsigned char)c)
      return (char *)s;
  return c == '\0' ? (char *)s : (void *)0;
}

/* sqrt — needed by wikisort; use the D-extension fsqrt.d instruction
 * directly rather than pulling in newlib's libm (which needs __errno). */
double
sqrt (double x)
{
  double r;
  __asm__ ("fsqrt.d %0, %1" : "=f" (r) : "f" (x));
  return r;
}

/* memmove — needed by qrduino */
void *
memmove (void *dst, const void *src, size_t n)
{
  unsigned char       *d = (unsigned char *)dst;
  const unsigned char *s = (const unsigned char *)src;
  if (d < s || d >= s + n)
    {
      for (size_t i = 0; i < n; i++) d[i] = s[i];
    }
  else
    {
      for (size_t i = n; i > 0; i--) d[i - 1] = s[i - 1];
    }
  return dst;
}
