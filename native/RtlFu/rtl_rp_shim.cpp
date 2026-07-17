// FFI shim wrapping a Verilator-compiled RTL cache replacement policy so
// RtlFfiReplacementPolicy (C#) can load it at runtime — the replacement-policy
// counterpart of rtl_fu_shim.cpp / rtl_bp_shim.cpp.
//
// The model to wrap is selected at build time via
//   -DRTL_MODEL_HEADER="\"VSrripRp.h\"" -DRTL_MODEL=VSrripRp
// (see build.sh) and must expose this port contract:
//
//   clock, reset
//   io_cfgSets, io_cfgWays                        (elaboration-time geometry, constant)
//   io_hitValid, io_hitSet, io_hitWay             (hit promotion, one clock edge)
//   io_instValid, io_instSet, io_instWay          (fill insertion, one clock edge)
//   io_victimValid, io_victimSet, io_victimWay    (victim comb.; state update on edge)
//   io_metaSet, io_metaWay, io_metaRrpv           (combinational metadata read)
//
// The C# side validates io_cfgSets/io_cfgWays against the cache it attaches the
// policy to — geometry is fixed when the Chisel is elaborated.

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

void* rtl_rp_create() {
    auto* m = new RTL_MODEL();
    m->io_hitValid = 0;
    m->io_instValid = 0;
    m->io_victimValid = 0;
    m->reset = 1;
    tick(m);
    tick(m);
    m->reset = 0;
    m->eval();
    return m;
}

void rtl_rp_destroy(void* handle) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->final();
    delete m;
}

void rtl_rp_geometry(void* handle, int* sets, int* ways) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->eval();
    *sets = static_cast<int>(m->io_cfgSets);
    *ways = static_cast<int>(m->io_cfgWays);
}

void rtl_rp_record_hit(void* handle, int set, int way) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->io_hitValid = 1;
    m->io_hitSet = set;
    m->io_hitWay = way;
    tick(m);
    m->io_hitValid = 0;
    m->eval();
}

int rtl_rp_choose_victim(void* handle, int set) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->io_victimValid = 1;
    m->io_victimSet = set;
    m->eval();
    int way = static_cast<int>(m->io_victimWay);
    tick(m); // apply the aging write-back
    m->io_victimValid = 0;
    m->eval();
    return way;
}

void rtl_rp_record_install(void* handle, int set, int way) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->io_instValid = 1;
    m->io_instSet = set;
    m->io_instWay = way;
    tick(m);
    m->io_instValid = 0;
    m->eval();
}

int rtl_rp_metadata(void* handle, int set, int way) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->io_metaSet = set;
    m->io_metaWay = way;
    m->eval();
    return static_cast<int>(m->io_metaRrpv);
}

}
