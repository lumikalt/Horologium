/*
Copyright 2018 Embedded Microprocessor Benchmark Consortium (EEMBC)

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.

Original Author: Shay Gal-on
*/

/* Horologium bare-metal port: RV32 + riscv-tests HTIF environment.
   Adapted from the upstream barebones/core_portme.h template. */

#ifndef CORE_PORTME_H
#define CORE_PORTME_H

#include <stddef.h>

/* No FPU-formatted output: the riscv-tests minimal printf has no %f,
   so timing is reported in raw mcycle ticks (secs_ret = ee_u32). */
#ifndef HAS_FLOAT
#define HAS_FLOAT 0
#endif

#ifndef HAS_TIME_H
#define HAS_TIME_H 0
#endif
#ifndef USE_CLOCK
#define USE_CLOCK 0
#endif
#ifndef HAS_STDIO
#define HAS_STDIO 0
#endif
#ifndef HAS_PRINTF
#define HAS_PRINTF 0
#endif

#ifndef COMPILER_VERSION
#ifdef __GNUC__
#define COMPILER_VERSION "GCC"__VERSION__
#else
#define COMPILER_VERSION "unknown"
#endif
#endif
#ifndef COMPILER_FLAGS
#define COMPILER_FLAGS FLAGS_STR
#endif
#ifndef MEM_LOCATION
#define MEM_LOCATION "STACK"
#endif

typedef signed short   ee_s16;
typedef unsigned short ee_u16;
typedef signed int     ee_s32;
typedef double         ee_f32;
typedef unsigned char  ee_u8;
typedef unsigned int   ee_u32;
typedef ee_u32         ee_ptr_int;
typedef size_t         ee_size_t;
#ifndef NULL
#define NULL ((void *)0)
#endif

#define align_mem(x) (void *)(4 + (((ee_ptr_int)(x)-1) & ~3))

/* Ticks are raw mcycle counts. A 32-bit counter overflows after ~4.3G
   cycles — far beyond any Horologium benchmark budget. */
#define CORETIMETYPE ee_u32
typedef ee_u32 CORE_TICKS;

#ifndef SEED_METHOD
#define SEED_METHOD SEED_VOLATILE
#endif

#ifndef MEM_METHOD
#define MEM_METHOD MEM_STACK
#endif

#ifndef MULTITHREAD
#define MULTITHREAD 1
#define USE_PTHREAD 0
#define USE_FORK    0
#define USE_SOCKET  0
#endif

/* syscalls.c calls main(0, 0); argc/argv are simply unused. */
#ifndef MAIN_HAS_NOARGC
#define MAIN_HAS_NOARGC 0
#endif
#ifndef MAIN_HAS_NORETURN
#define MAIN_HAS_NORETURN 0
#endif

extern ee_u32 default_num_contexts;

typedef struct CORE_PORTABLE_S
{
    ee_u8 portable_id;
} core_portable;

/* target specific init/fini */
void portable_init(core_portable *p, int *argc, char *argv[]);
void portable_fini(core_portable *p);

#if !defined(PROFILE_RUN) && !defined(PERFORMANCE_RUN) \
    && !defined(VALIDATION_RUN)
#if (TOTAL_DATA_SIZE == 1200)
#define PROFILE_RUN 1
#elif (TOTAL_DATA_SIZE == 2000)
#define PERFORMANCE_RUN 1
#else
#define VALIDATION_RUN 1
#endif
#endif

int ee_printf(const char *fmt, ...);

#endif /* CORE_PORTME_H */
