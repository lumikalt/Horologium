// FFI shim wrapping a Verilator-compiled RTL branch predictor so
// RtlFfiBranchPredictor (C#) can load it at runtime — the branch-predictor
// counterpart of rtl_fu_shim.cpp.
//
// The model to wrap is selected at build time via
//   -DRTL_MODEL_HEADER="\"VGshareBp.h\"" -DRTL_MODEL=VGshareBp
// (see build.sh) and must expose this port contract:
//
//   clock, reset
//   io_predPc, io_predTaken, io_predTarget          (combinational lookup)
//   io_updValid, io_updPc, io_updTaken, io_updTarget (applied on one clock edge)
//
// Prediction is a pure combinational read — rtl_bp_predict evaluates without
// clocking, exactly like a fetch-stage table lookup. rtl_bp_update drives the
// update ports for a single clock edge, the commit-time training write.

#include "verilated.h"
#include RTL_MODEL_HEADER

extern "C" {

void* rtl_bp_create() {
    auto* m = new RTL_MODEL();
    m->io_updValid = 0;
    m->reset = 1;
    m->clock = 0; m->eval();
    m->clock = 1; m->eval();
    m->clock = 0; m->eval();
    m->clock = 1; m->eval();
    m->reset = 0;
    m->eval();
    return m;
}

void rtl_bp_destroy(void* handle) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->final();
    delete m;
}

void rtl_bp_predict(void* handle, unsigned long long pc, int* taken, unsigned long long* target) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->io_predPc = static_cast<unsigned>(pc);
    m->eval();
    *taken = m->io_predTaken ? 1 : 0;
    *target = m->io_predTarget;
}

void rtl_bp_update(void* handle, unsigned long long pc, int taken, unsigned long long target) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->io_updValid = 1;
    m->io_updPc = static_cast<unsigned>(pc);
    m->io_updTaken = taken ? 1 : 0;
    m->io_updTarget = static_cast<unsigned>(target);
    m->clock = 0; m->eval();
    m->clock = 1; m->eval();
    m->io_updValid = 0;
    m->eval();
}

}
