// Generic FFI shim for CBP2025/CBP-NG (AmpereComputing/cbp-ng) predictor submissions —
// the harcom hardware-timing-modeling DSL, distinct from the CBP-3/CBP-5 `class PREDICTOR`
// idiom that native/CbpShim/ wraps.
//
// cbp-ng's own reference harness (`class harcom_superuser` in the real cbp-ng repo's
// cbp.hpp) pulls instructions from a gzipped trace file and always fully resolves one
// prediction block (predict -> update_condbr -> update_cycle) before starting the next.
// This shim instead PUSHES predict/update calls driven live from Horologium, one call at
// a time via cbpng_predict/cbpng_update below.
//
// Correctness note (see TODO.md "CBP2025/CBP-NG predictor integration"): predictors keep
// per-block state (index/prediction registers written by predict1/predict2) that
// update_condbr/update_cycle read back later. If a second block is predicted before the
// first one's update fires, those registers get clobbered and training silently corrupts.
// This shim itself does not defend against that — CbpNgFfiPredictor (C#) is responsible
// for only driving one outstanding block at a time (see its "pending" tracking), which is
// why this integration is scoped to in-order/shallow pipelines.
//
// The header/type to wrap is selected at build time via -DCBPNG_PREDICTOR_HEADER and
// -DCBPNG_PREDICTOR_TYPE (see build.sh), since cbp-ng predictors are templated structs
// named after the technique (e.g. `bimodal<6,17,12>`), not a fixed `class PREDICTOR`.

#include <cstdint>

#include "cbp.hpp"
#include CBPNG_PREDICTOR_HEADER

using namespace hcm;

static constexpr uint64_t CBPNG_CYCLE_PS = 300;

// BranchKind bit values, mirrored from Mechanism.BranchKind (C#) — see IBranchPredictor.cs.
static constexpr int KIND_CONDITIONAL = 1 << 0;
static constexpr int KIND_CALL = 1 << 1;
static constexpr int KIND_RETURN = 1 << 2;
static constexpr int KIND_INDIRECT = 1 << 3;

// Named `harcom_superuser` (global namespace) to satisfy harcom.hpp's hardcoded
// `friend class ::harcom_superuser;` grants on val<N,T>::get()/get_vt() and
// globals::next_cycle() — see cbp.hpp's header comment.
class harcom_superuser {
public:
    predictor *p;
    bool pendingTaken = false;
    uint64_t time = 0;

    explicit harcom_superuser(predictor *pr) : p(pr) {
        panel.clock_cycle_ps = CBPNG_CYCLE_PS;
        panel.make_floorplan();
        p->reuse_prediction_callback = [](val<1>) {};
        p->need_extra_cycle_callback = [](val<1> yes) {
            if (yes) panel.next_cycle();
        };
    }

    // Always predicts fresh (predict1 then predict2), never reuse_predict1/2 — Horologium
    // has no notion of "reuse the same fetch block for the next instruction", so every
    // branch starts its own prediction block. p2 is taken as the authoritative direction,
    // matching cbp-ng's own reference harness (p2 vs. ground truth drives its accuracy
    // stats; p1/p2 disagreement is tracked only as a secondary "short misprediction").
    bool do_predict(uint64_t pc) {
        p->predict1(val<64>{pc, time});
        val<1> p2 = p->predict2(val<64>{pc, time});
        bool taken = p2;
        pendingTaken = taken;
        return taken;
    }

    void do_update(uint64_t pc, bool taken, uint64_t nextPc, int kind) {
        bool mispredicted = pendingTaken != taken;
        bool conditional = (kind & KIND_CONDITIONAL) != 0;

        if (conditional) {
            p->update_condbr(val<64>{pc, time}, val<1>{taken, time}, val<64>{nextPc, time});
        }

        instruction_info info;
        info.is_branch = val<1>{true, time};
        info.is_taken = val<1>{taken, time};
        info.is_conditional = val<1>{conditional, time};
        info.is_indirect = val<1>{(kind & KIND_INDIRECT) != 0, time};
        info.is_call = val<1>{(kind & KIND_CALL) != 0, time};
        info.is_return = val<1>{(kind & KIND_RETURN) != 0, time};
        info.is_mispredict = val<1>{mispredicted, time};
        info.next_pc = val<64>{nextPc, time};

        p->update_cycle(info);
        time += CBPNG_CYCLE_PS;
        panel.next_cycle();
    }
};

extern "C" {

__attribute__((visibility("default"))) void *cbpng_create() {
    auto *pred = new CBPNG_PREDICTOR_TYPE();
    return new harcom_superuser(pred);
}

__attribute__((visibility("default"))) void cbpng_destroy(void *handle) {
    auto *hs = static_cast<harcom_superuser *>(handle);
    delete hs->p;
    delete hs;
}

__attribute__((visibility("default"))) int cbpng_predict(void *handle, unsigned long long pc) {
    auto *hs = static_cast<harcom_superuser *>(handle);
    return hs->do_predict(static_cast<uint64_t>(pc)) ? 1 : 0;
}

__attribute__((visibility("default"))) void cbpng_update(
    void *handle,
    unsigned long long pc,
    int taken,
    unsigned long long nextPc,
    int kind
) {
    auto *hs = static_cast<harcom_superuser *>(handle);
    hs->do_update(static_cast<uint64_t>(pc), taken != 0, static_cast<uint64_t>(nextPc), kind);
}

}
