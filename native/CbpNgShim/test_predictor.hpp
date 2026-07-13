// Minimal harcom `predictor` used only to exercise cbp_ng_shim.cpp end-to-end
// (CbpNgFfiBranchPredictionTests). Not a competitive predictor — a single global
// 1-bit direction register tracking the last resolved outcome, nothing more.
// Deliberately avoids `ram` (a per-PC table): cbp_ng_shim.cpp resolves a branch
// in the same hardware cycle it was predicted in (no next_cycle() between predict
// and update), and harcom terminates on a same-cycle read+write of the same ram —
// `reg` has no such restriction (only "one write per cycle" to the same register,
// which this predictor never does).
#include "../cbp.hpp"
#include "../harcom.hpp"

using namespace hcm;

struct testbimodal : predictor {
    reg<1> pred;

    val<1> predict1([[maybe_unused]] val<64> inst_pc) { return pred; }
    val<1> reuse_predict1([[maybe_unused]] val<64> inst_pc) { return pred; }
    val<1> predict2([[maybe_unused]] val<64> inst_pc) { return pred; }
    val<1> reuse_predict2([[maybe_unused]] val<64> inst_pc) { return pred; }

    void update_condbr(
        [[maybe_unused]] val<64> branch_pc,
        [[maybe_unused]] val<1> taken,
        [[maybe_unused]] val<64> next_pc
    ) {}

    void update_cycle(instruction_info &block_end_info) {
        pred = block_end_info.is_taken;
    }
};
