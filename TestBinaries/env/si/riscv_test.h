// Redirect: supervisor tests pick up riscv_test_s.h via -I env/si -I env
// (env/si comes first in the include path, so "riscv_test.h" resolves here)
#include "../riscv_test_s.h"
