// FFI shim wrapping a Verilator-compiled RTL floating-point functional unit that
// reports IEEE exception flags alongside its result — the flags-carrying variant of
// rtl_fu_shim.cpp, loaded through RtlFfiFunctionalUnit's rtl_execute_flags export.
//
// The model to wrap is selected at build time via
//   -DRTL_MODEL_HEADER="\"VFDivSqrtUnit.h\"" -DRTL_MODEL=VFDivSqrtUnit
// (see build.sh) and must expose the FU port contract with a flags field:
//
//   clock, reset
//   io_req_ready, io_req_valid, io_req_bits_op, io_req_bits_a, io_req_bits_b
//   io_resp_valid, io_resp_bits_result, io_resp_bits_flags
//
// Cycle counting matches rtl_fu_shim: clock edges from request acceptance until
// io_resp_valid, reported as the instruction's FU latency.

#include "verilated.h"
#include RTL_MODEL_HEADER

namespace {

void tick(RTL_MODEL* m) {
    m->clock = 0;
    m->eval();
    m->clock = 1;
    m->eval();
}

} // namespace

extern "C" {

void* rtl_create() {
    auto* m = new RTL_MODEL();
    m->io_req_valid = 0;
    m->reset = 1;
    tick(m);
    tick(m);
    m->reset = 0;
    m->eval();
    return m;
}

void rtl_destroy(void* handle) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->final();
    delete m;
}

// Returns 0 on success, -1 if the unit hung (guards far beyond any legal latency).
int rtl_execute_flags(
    void* handle,
    unsigned op,
    unsigned a,
    unsigned b,
    unsigned* result,
    unsigned* flags,
    int* cycles
) {
    auto* m = static_cast<RTL_MODEL*>(handle);

    m->eval();
    for (int guard = 0; !m->io_req_ready; guard++) {
        if (guard > 64) return -1;
        tick(m);
    }

    m->io_req_valid = 1;
    m->io_req_bits_op = op;
    m->io_req_bits_a = a;
    m->io_req_bits_b = b;
    tick(m); // acceptance edge
    m->io_req_valid = 0;

    int n = 1;
    while (!m->io_resp_valid) {
        if (n > 4096) return -1;
        tick(m);
        n++;
    }

    *result = m->io_resp_bits_result;
    *flags = m->io_resp_bits_flags;
    *cycles = n;
    return 0;
}

}
