// Minimal class PREDICTOR used only to exercise cbp_shim.cpp end-to-end
// (CbpFfiBranchPredictionTests). Not a competitive predictor — a per-PC 2-bit
// saturating counter, nothing more.

#ifndef TEST_PREDICTOR_H
#define TEST_PREDICTOR_H

#include "utils.h"
#include <unordered_map>

class PREDICTOR {
public:
    PREDICTOR(void) {}

    bool GetPrediction(UINT64 PC) {
        auto it = counters.find(PC);
        return it != counters.end() && it->second >= 2;
    }

    void UpdatePredictor(UINT64 PC, OpType opType, bool resolveDir, bool predDir, UINT64 branchTarget) {
        auto it = counters.find(PC);
        UINT32 c = it != counters.end() ? it->second : 1;
        counters[PC] = resolveDir ? SatIncrement(c, 3) : SatDecrement(c);
    }

    void TrackOtherInst(UINT64 PC, OpType opType, bool taken, UINT64 branchTarget) {}

private:
    std::unordered_map<UINT64, UINT32> counters;
};

#endif
