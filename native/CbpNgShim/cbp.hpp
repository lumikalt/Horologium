// Minimal, API-compatible reimplementation of cbp-ng's cbp.hpp submission interface
// (AmpereComputing/cbp-ng, MIT licensed — see vendor/harcom.hpp for the original's
// license header). Predictor submissions (predictors/*.hpp in a cbp-ng checkout)
// #include "../cbp.hpp" and "../harcom.hpp" and reference only `predictor`,
// `instruction_info`, and `callback` from the former — never the reference
// trace-driven harness (cbp-ng's own `class harcom_superuser`, which pulls
// instructions from a gzipped trace file via `trace_reader` and is not
// reusable for a live, push-based simulator like Horologium).
//
// This header supplies exactly those three types so unmodified predictor
// submissions compile against it, while cbp_ng_shim.cpp defines its own
// `harcom_superuser`-named driving class (harcom.hpp grants raw value/clock
// access only to a class with that literal name, via hardcoded `friend`
// declarations) that pushes predict/update calls from C# instead of pulling
// them from a trace file.
#pragma once
#include <functional>
#include "harcom.hpp"

using namespace hcm;

using callback = std::function<void(val<1>)>;

struct instruction_info {
    val<1> is_branch;
    val<1> is_taken;
    val<1> is_conditional;
    val<1> is_indirect;
    val<1> is_call;
    val<1> is_return;
    val<1> is_mispredict;
    val<64> next_pc;
};

struct predictor {
    friend class ::harcom_superuser;

    virtual val<1> predict1(val<64> inst_pc) = 0;
    virtual val<1> reuse_predict1(val<64> inst_pc) = 0;
    virtual val<1> predict2(val<64> inst_pc) = 0;
    virtual val<1> reuse_predict2(val<64> inst_pc) = 0;
    virtual void update_condbr(val<64> branch_pc, val<1> taken, val<64> next_pc) = 0;
    virtual void update_cycle(instruction_info &block_end_info) = 0;

    void reuse_prediction(val<1> reuse_next) {
        reuse_prediction_callback(reuse_next.fo1());
    }

    void need_extra_cycle(val<1> yes) {
        need_extra_cycle_callback(yes.fo1());
    }

    virtual ~predictor() {}

private:
    callback need_extra_cycle_callback;
    callback reuse_prediction_callback;
};
