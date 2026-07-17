// FFI shim wrapping a Verilator-compiled RTL cache prefetcher so RtlFfiPrefetcher
// (C#) can load it at runtime — the prefetcher counterpart of rtl_fu_shim.cpp /
// rtl_bp_shim.cpp / rtl_rp_shim.cpp.
//
// The model to wrap is selected at build time via
//   -DRTL_MODEL_HEADER="\"VStridePf.h\"" -DRTL_MODEL=VStridePf
// (see build.sh) and must expose this port contract:
//
//   clock, reset
//   io_cfgTableSize                                  (elaboration-time size, constant)
//   io_accValid, io_accPc, io_accAddr, io_accHit     (one demand access)
//   io_prefValid, io_prefAddr                        (combinational prefetch decision)
//
// rtl_pf_access presents one demand access: the prefetch decision is read
// combinationally (post-update semantics live in the model), then the table
// update commits on one clock edge. Returns the number of prefetch addresses
// written (0 or 1 with this contract).

#include "verilated.h"
#include RTL_MODEL_HEADER

extern "C" {

void* rtl_pf_create() {
    auto* m = new RTL_MODEL();
    m->io_accValid = 0;
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
    m->eval();

    int count = 0;
    if (m->io_prefValid && capacity > 0) {
        targets[0] = m->io_prefAddr;
        count = 1;
    }

    m->clock = 0; m->eval();
    m->clock = 1; m->eval(); // commit the table update
    m->io_accValid = 0;
    m->eval();
    return count;
}

}
