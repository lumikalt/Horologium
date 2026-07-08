# Minimal startup for Linux SE mode (no M-mode CSRs, no HTIF).
# gem5 SE mode sets up the stack for us; we don't need to set sp.
# The benchmarks ignore argc/argv, so we pass zeroes and call main directly.

  .section ".text.init"
  .globl _start
_start:
  # Clear all integer registers (deterministic startup, matching crt.S style)
  # gp (x3) is initialised AFTER clearing — do not touch x3 here.
  li  x1,  0
  # x2 = sp: do not zero — gem5 SE mode pre-initialises the stack pointer
  # x3 = gp: initialised below
  li  x4,  0
  li  x5,  0
  li  x6,  0
  li  x7,  0
  li  x8,  0
  li  x9,  0
  li  x10, 0
  li  x11, 0
  li  x12, 0
  li  x13, 0
  li  x14, 0
  li  x15, 0
  li  x16, 0
  li  x17, 0
  li  x18, 0
  li  x19, 0
  li  x20, 0
  li  x21, 0
  li  x22, 0
  li  x23, 0
  li  x24, 0
  li  x25, 0
  li  x26, 0
  li  x27, 0
  li  x28, 0
  li  x29, 0
  li  x30, 0
  li  x31, 0

  # Initialise the global pointer now that all other regs are clear.
  .option push
  .option norelax
  la gp, __global_pointer$
  .option pop

  # call main(0, 0) — benchmarks ignore argc/argv
  li  a0, 0
  li  a1, 0
  call main

  # exit(return_value) — a0 already holds main's return value
  li  a7, 93   # SYS_exit
  ecall

  # should never reach here
1:j 1b
