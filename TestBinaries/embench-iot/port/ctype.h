/* Minimal bare-metal ctype.h for Embench-IoT port.
 * Shadows the system ctype.h to avoid newlib's _ctype_ table dependency.
 * Only the functions actually used by Embench benchmarks are defined. */

#ifndef _PORT_CTYPE_H
#define _PORT_CTYPE_H

#include <stddef.h>

static inline int isdigit(int c) { return c >= '0' && c <= '9'; }
static inline int isalpha(int c) {
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}
static inline int islower(int c) { return c >= 'a' && c <= 'z'; }
static inline int isupper(int c) { return c >= 'A' && c <= 'Z'; }
static inline int isspace(int c) {
    return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\v';
}
static inline int isalnum(int c) { return isdigit(c) || isalpha(c); }
static inline int isxdigit(int c) {
    return isdigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
static inline int isprint(int c)  { return c >= ' ' && c <= '~'; }
static inline int ispunct(int c)  { return isprint(c) && !isalnum(c) && c != ' '; }
static inline int iscntrl(int c)  { return (c >= 0 && c < ' ') || c == 127; }
static inline int tolower(int c)  { return isupper(c) ? c + ('a' - 'A') : c; }
static inline int toupper(int c)  { return islower(c) ? c - ('a' - 'A') : c; }

#endif /* _PORT_CTYPE_H */
