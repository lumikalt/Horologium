// FFI shim wrapping a Verilator-compiled multi-degree RTL cache prefetcher —
// the multi-target extension of rtl_pf_shim.cpp, exposing the SAME rtl_pf_* C ABI
// so RtlFfiPrefetcher (C#) loads either kind unchanged.
//
// The model to wrap is selected at build time via
//   -DRTL_MODEL_HEADER="\"VStreamPf.h\"" -DRTL_MODEL=VStreamPf
// (see build.sh) and must expose this port contract:
//
//   clock, reset
//   io_cfgTableSize                                  (elaboration-time size, constant)
//   io_accValid, io_accPc, io_accAddr, io_accHit     (one demand access, one clock edge)
//   io_drainValid, io_drainAddr, io_drainPop         (prefetch drain queue: the access
//                                                     edge loads it; asserting drainPop
//                                                     pops one entry per edge)
//
// rtl_pf_access presents the access for one edge, then drains the queue one address
// per clock until empty or the caller's buffer is full. Undrained entries (only
// possible when the buffer is smaller than the model's burst degree) are dropped by
// the next access.

#include "verilated.h"
#include RTL_MODEL_HEADER

extern "C" {

void* rtl_pf_create() {
    auto* m = new RTL_MODEL();
    m->io_accValid = 0;
    m->io_drainPop = 0;
    m->reset = 1;
    m->clock = 0; m->eval();
    m->clock = 1; m->eval();
    m->clock = 0; m->eval();
    m->clock = 1; m->eval();
    m->reset = 0;
    m->eval();
    return m;
}

void rtl_pf_destroy(void* handle) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->final();
    delete m;
}

int rtl_pf_access(
    void* handle,
    unsigned long long pc,
    unsigned long long addr,
    int wasHit,
    unsigned long long* targets,
    int capacity
) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->io_accValid = 1;
    m->io_accPc = pc;
    m->io_accAddr = addr;
    m->io_accHit = wasHit ? 1 : 0;
    m->io_drainPop = 0;
    m->clock = 0; m->eval();
    m->clock = 1; m->eval(); // access edge: table update + queue load
    m->io_accValid = 0;

    int n = 0;
    m->io_drainPop = 1;
    m->eval();
    while (n < capacity && m->io_drainValid) {
        targets[n++] = m->io_drainAddr;
        m->clock = 0; m->eval();
        m->clock = 1; m->eval(); // pop edge
    }

    m->io_drainPop = 0;
    m->eval();
    return n;
}

}
