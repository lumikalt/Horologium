// FFI shim wrapping a Verilator-compiled RTL branch predictor that manages its own
// speculative global history — the history-tracking extension of rtl_bp_shim.cpp,
// loaded by RtlFfiHistoryBranchPredictor (C#).
//
// The model to wrap is selected at build time via
//   -DRTL_MODEL_HEADER="\"VLTageBp.h\"" -DRTL_MODEL=VLTageBp
// (see build.sh) and must expose this port contract:
//
//   clock, reset
//   io_predPc, io_predTaken                  (combinational lookup; direction only —
//                                             targets live in the C# wrapper's BTB)
//   io_updValid, io_updPc, io_updTaken       (commit-time training, one clock edge)
//   io_specValid, io_specTaken               (fold predicted direction at fetch, one edge)
//   io_recoverValid                          (flush: restore committed history, one edge)
//   io_histOut                               (combinational working-history read = capture)
//   io_restValid, io_restHist, io_restTaken  (partial squash: restore checkpoint and fold
//                                             the resolved direction, one edge)
//
// The working history IS the checkpoint (TAGE-family folds derive from it), so
// capture/restore move a single 64-bit value across the FFI — no checkpoint RAM.

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

void* rtl_hbp_create() {
    auto* m = new RTL_MODEL();
    m->io_updValid = 0;
    m->io_specValid = 0;
    m->io_recoverValid = 0;
    m->io_restValid = 0;
    m->reset = 1;
    tick(m);
    tick(m);
    m->reset = 0;
    m->eval();
    return m;
}

void rtl_hbp_destroy(void* handle) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->final();
    delete m;
}

int rtl_hbp_predict(void* handle, unsigned long long pc) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->io_predPc = pc;
    m->eval();
    return m->io_predTaken ? 1 : 0;
}

void rtl_hbp_update(void* handle, unsigned long long pc, int taken) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->io_updValid = 1;
    m->io_updPc = pc;
    m->io_updTaken = taken ? 1 : 0;
    tick(m);
    m->io_updValid = 0;
    m->eval();
}

void rtl_hbp_spec_update(void* handle, int taken) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->io_specValid = 1;
    m->io_specTaken = taken ? 1 : 0;
    tick(m);
    m->io_specValid = 0;
    m->eval();
}

void rtl_hbp_recover(void* handle) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->io_recoverValid = 1;
    tick(m);
    m->io_recoverValid = 0;
    m->eval();
}

unsigned long long rtl_hbp_history(void* handle) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->eval();
    return m->io_histOut;
}

void rtl_hbp_restore(void* handle, unsigned long long hist, int taken) {
    auto* m = static_cast<RTL_MODEL*>(handle);
    m->io_restValid = 1;
    m->io_restHist = hist;
    m->io_restTaken = taken ? 1 : 0;
    tick(m);
    m->io_restValid = 0;
    m->eval();
}

}
