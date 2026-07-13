// Generic FFI shim for CBP-3/CBP-5 (2016)-style `class PREDICTOR` submissions.
//
// A submission's predictor.h is expected to `#include "utils.h"` and define:
//
//   class PREDICTOR {
//   public:
//     PREDICTOR(void);
//     bool GetPrediction(UINT64 PC);
//     void UpdatePredictor(UINT64 PC, OpType opType, bool resolveDir, bool predDir, UINT64 branchTarget);
//     void TrackOtherInst(UINT64 PC, OpType opType, bool taken, UINT64 branchTarget);
//   };
//
// The header to wrap is selected at build time via -DCBP_PREDICTOR_HEADER="path/to/predictor.h"
// (see build.sh). This shim only drives GetPrediction/UpdatePredictor — Horologium's
// IBranchPredictor has no non-branch instruction stream to feed TrackOtherInst, and no
// call-type classification, so every branch is reported as OPTYPE_JMP_DIRECT_COND (the
// generic conditional-direct-branch type; Horologium's own built-in predictors make the
// same simplification, since ITooth only distinguishes Branch/ConditionalBranch, not the
// full CBP OpType taxonomy).

#include "utils.h"
#include CBP_PREDICTOR_HEADER

extern "C" {

void* cbp_create() {
    return new PREDICTOR();
}

void cbp_destroy(void* handle) {
    delete static_cast<PREDICTOR*>(handle);
}

int cbp_predict(void* handle, unsigned long long pc) {
    return static_cast<PREDICTOR*>(handle)->GetPrediction(static_cast<UINT64>(pc)) ? 1 : 0;
}

void cbp_update(
    void* handle,
    unsigned long long pc,
    int resolveDir,
    int predDir,
    unsigned long long target
) {
    static_cast<PREDICTOR*>(handle)->UpdatePredictor(
        static_cast<UINT64>(pc),
        OPTYPE_JMP_DIRECT_COND,
        resolveDir != 0,
        predDir != 0,
        static_cast<UINT64>(target)
    );
}

}
