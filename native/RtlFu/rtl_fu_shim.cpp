// Generic FFI shim wrapping a Verilator-compiled RTL functional unit so
// RtlFfiFunctionalUnit (C#) can load it at runtime — the RTL counterpart of
// native/CbpShim for branch predictors.
//
// The model to wrap is selected at build time via
//   -DRTL_MODEL_HEADER="\"VDivUnit.h\"" -DRTL_MODEL=VDivUnit
// (see build.sh) and must expose this port contract (a Chisel module with
// io.req = Flipped(Decoupled(op/a/b)) and io.resp = Valid(UInt) produces it):
//
//   clock, reset
//   io_req_ready, io_req_valid, io_req_bits_op, io_req_bits_a, io_req_bits_b
//   io_resp_valid, io_resp_bits
//
// rtl_execute drives one operation to completion and reports the observed
// cycle count (clock edges from request acceptance until io_resp_valid),
// which the pipeline uses as the instruction's FU latency. Cycles spent
// waiting for io_req_ready are not counted: they model shim-level
// serialization, not the latency a pipelined wrapper would expose.

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

// Returns 0 on success, -1 if the unit never became ready or never produced
// a result (a hung model; the guard bounds are far beyond any legal latency).
int rtl_execute(
    void* handle,
    unsigned op,
    unsigned a,
    unsigned b,
    unsigned* result,
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

    *result = m->io_resp_bits;
    *cycles = n;
    return 0;
}

}
