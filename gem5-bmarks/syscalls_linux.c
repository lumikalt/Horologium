// Linux-ABI syscall layer for gem5 SE benchmark variants.
// Replaces riscv-tests/benchmarks/common/syscalls.c:
//   - exit via Linux ecall (SYS_exit = 93), not HTIF tohost write
//   - setStats() is a no-op (gem5 stats file captures cycle/inst counts)
//   - write() via Linux ecall (SYS_write = 64) for printstr/printf
//   - No M-mode CSRs, no HTIF memory-mapped I/O
//
// This file is self-contained: it supplies memcpy, memset, strlen, printf,
// sprintf, etc. that the benchmarks include via -nostdlib.

#include <stdint.h>
#include <string.h>
#include <stdarg.h>
#include <limits.h>
#include <sys/signal.h>

// ── Linux syscall stubs ───────────────────────────────────────────────────────

static long _linux_write(int fd, const void *buf, size_t count) {
    register long a0 asm("a0") = fd;
    register long a1 asm("a1") = (long)buf;
    register long a2 asm("a2") = (long)count;
    register long a7 asm("a7") = 64; // SYS_write
    asm volatile("ecall" : "+r"(a0) : "r"(a1), "r"(a2), "r"(a7));
    return a0;
}

static void __attribute__((noreturn)) _linux_exit(int code) {
    register int a0 asm("a0") = code;
    register int a7 asm("a7") = 93; // SYS_exit
    asm volatile("ecall" :: "r"(a0), "r"(a7));
    __builtin_unreachable();
}

// ── Public API (matching riscv-tests/benchmarks/common/syscalls.c) ───────────

// setStats: wire gem5 ROI markers so stats cover only the kernel interval.
//
// gem5 RISC-V pseudo-ops use opcode 0x7b (custom-3) with the m5op function
// code in bits [31:25] and x0 in all register fields.  gem5 reads arguments
// from the ABI registers (a0/a1) out-of-band; the register-pinned variables
// ensure the compiler keeps those registers loaded with 0 at the asm site.
//
//   M5OP_RESET_STATS = 0x40  →  0x0000007b | (0x40 << 25) = 0x8000007b
//   M5OP_EXIT        = 0x21  →  0x0000007b | (0x21 << 25) = 0x4200007b
void setStats(int enable) {
    if (enable) {
        register long _a0 asm("a0") = 0;  // delay  = 0 ticks
        register long _a1 asm("a1") = 0;  // period = 0 (one-shot)
        asm volatile(".long 0x8000007b" :: "r"(_a0), "r"(_a1));
    } else {
        register long _a0 asm("a0") = 0;  // exit code
        register long _a1 asm("a1") = 0;
        asm volatile(".long 0x4200007b" :: "r"(_a0), "r"(_a1));
    }
}

void __attribute__((noreturn)) tohost_exit(uintptr_t code) {
    _linux_exit((int)code);
}

void exit(int code) {
    _linux_exit(code);
}

void abort(void) {
    _linux_exit(128 + SIGABRT);
}

void printstr(const char *s) {
    size_t n = 0;
    while (s[n]) n++;
    _linux_write(1, s, n);
}

// ── Minimal string/memory functions (copied from upstream syscalls.c) ─────────

void *memcpy(void *dest, const void *src, size_t len) {
    if ((((uintptr_t)dest | (uintptr_t)src | len) & (sizeof(uintptr_t)-1)) == 0) {
        const uintptr_t *s = src;
        uintptr_t *d = dest;
        uintptr_t *end = dest + len;
        while (d + 8 < end) {
            uintptr_t reg[8] = {s[0],s[1],s[2],s[3],s[4],s[5],s[6],s[7]};
            d[0]=reg[0]; d[1]=reg[1]; d[2]=reg[2]; d[3]=reg[3];
            d[4]=reg[4]; d[5]=reg[5]; d[6]=reg[6]; d[7]=reg[7];
            d += 8; s += 8;
        }
        while (d < end) *d++ = *s++;
    } else {
        const char *s = src;
        char *d = dest;
        while (d < (char *)(dest + len)) *d++ = *s++;
    }
    return dest;
}

void *memset(void *dest, int byte, size_t len) {
    if ((((uintptr_t)dest | len) & (sizeof(uintptr_t)-1)) == 0) {
        uintptr_t word = byte & 0xFF;
        word |= word << 8;
        word |= word << 16;
        uintptr_t *d = dest;
        while (d < (uintptr_t *)(dest + len)) *d++ = word;
    } else {
        char *d = dest;
        while (d < (char *)(dest + len)) *d++ = (char)byte;
    }
    return dest;
}

size_t strlen(const char *s) {
    const char *p = s;
    while (*p) p++;
    return p - s;
}

size_t strnlen(const char *s, size_t n) {
    const char *p = s;
    while (n-- && *p) p++;
    return p - s;
}

int strcmp(const char *s1, const char *s2) {
    unsigned char c1, c2;
    do { c1 = *s1++; c2 = *s2++; } while (c1 && c1 == c2);
    return c1 - c2;
}

char *strcpy(char *dest, const char *src) {
    char *d = dest;
    while ((*d++ = *src++));
    return dest;
}

long atol(const char *str) {
    long res = 0;
    int sign = 0;
    while (*str == ' ') str++;
    if (*str == '-' || *str == '+') { sign = (*str == '-'); str++; }
    while (*str) { res = res * 10 + (*str++ - '0'); }
    return sign ? -res : res;
}

// ── printf / sprintf (matching upstream syscalls.c) ──────────────────────────

static void printnum(void (*putch)(int, void **), void **putdat,
                     unsigned long long num, unsigned base, int width, int padc) {
    unsigned digs[sizeof(num)*CHAR_BIT];
    int pos = 0;
    while (1) { digs[pos++] = num % base; if (num < base) break; num /= base; }
    while (width-- > pos) putch(padc, putdat);
    while (pos-- > 0) putch(digs[pos] + (digs[pos] >= 10 ? 'a'-10 : '0'), putdat);
}

static unsigned long long getuint(va_list *ap, int lflag) {
    if (lflag >= 2) return va_arg(*ap, unsigned long long);
    else if (lflag) return va_arg(*ap, unsigned long);
    else return va_arg(*ap, unsigned int);
}

static long long getint(va_list *ap, int lflag) {
    if (lflag >= 2) return va_arg(*ap, long long);
    else if (lflag) return va_arg(*ap, long);
    else return va_arg(*ap, int);
}

static void vprintfmt(void (*putch)(int, void **), void **putdat,
                      const char *fmt, va_list ap) {
    const char *p, *last_fmt;
    int ch;
    unsigned long long num;
    int base, lflag, width, precision, altflag;
    char padc;

    while (1) {
        while ((ch = *(unsigned char *)fmt) != '%') {
            if (ch == '\0') return;
            fmt++;
            putch(ch, putdat);
        }
        fmt++;
        last_fmt = fmt;
        padc = ' '; width = -1; precision = -1; lflag = 0; altflag = 0;
    reswitch:
        switch (ch = *(unsigned char *)fmt++) {
        case '-': padc = '-'; goto reswitch;
        case '0': padc = '0'; goto reswitch;
        case '1' ... '9':
            for (precision = 0; ; ++fmt) {
                precision = precision * 10 + ch - '0';
                ch = *fmt;
                if (ch < '0' || ch > '9') break;
            }
            goto process_precision;
        case '*': precision = va_arg(ap, int); goto process_precision;
        case '.': if (width < 0) width = 0; goto reswitch;
        case '#': altflag = 1; goto reswitch;
        process_precision:
            if (width < 0) { width = precision; precision = -1; }
            goto reswitch;
        case 'l': lflag++; goto reswitch;
        case 'c': putch(va_arg(ap, int), putdat); break;
        case 's':
            if ((p = va_arg(ap, char *)) == NULL) p = "(null)";
            if (width > 0 && padc != '-')
                for (width -= strnlen(p, precision); width > 0; width--) putch(padc, putdat);
            for (; (ch = *p) != '\0' && (precision < 0 || --precision >= 0); width--) {
                putch(ch, putdat); p++;
            }
            for (; width > 0; width--) putch(' ', putdat);
            break;
        case 'd':
            num = getint(&ap, lflag);
            if ((long long)num < 0) { putch('-', putdat); num = -(long long)num; }
            base = 10; goto signed_number;
        case 'u': base = 10; goto unsigned_number;
        case 'o': base = 8; goto unsigned_number;
        case 'p':
            lflag = 1;
            putch('0', putdat); putch('x', putdat);
            /* fall through */
        case 'x': base = 16;
        unsigned_number:
            num = getuint(&ap, lflag);
        signed_number:
            printnum(putch, putdat, num, base, width, padc);
            break;
        case '%': putch(ch, putdat); break;
        default: putch('%', putdat); fmt = last_fmt; break;
        }
    }
}

#undef putchar
int putchar(int ch) {
    char c = (char)ch;
    _linux_write(1, &c, 1);
    return ch;
}

static void putchar_wrap(int c, void **d) { (void)d; putchar(c); }

int printf(const char *fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    vprintfmt(putchar_wrap, NULL, fmt, ap);
    va_end(ap);
    return 0;
}

static void sprintf_putch(int ch, void **data) {
    char **pstr = (char **)data;
    **pstr = (char)ch;
    (*pstr)++;
}

int sprintf(char *str, const char *fmt, ...) {
    va_list ap;
    char *str0 = str;
    va_start(ap, fmt);
    vprintfmt(sprintf_putch, (void **)&str, fmt, ap);
    *str = 0;
    va_end(ap);
    return str - str0;
}
