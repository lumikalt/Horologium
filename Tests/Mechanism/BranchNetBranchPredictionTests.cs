using Mechanism;
using Mechanism.BranchPredictModels;

namespace Tests.Mechanism;

public class BranchNetBranchPredictionTests {
    [Fact]
    public void ColdMiss_PredictsFallThrough() {
        var p = new BranchNetPredictor();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
        Assert.Equal(0, p.TrainedModelCount);
    }

    [Fact]
    public void NoModels_BehavesLikeTageScL() {
        // With no FromProfile pre-pass, every branch falls through to the internal TAGE-SC-L
        // baseline unchanged.
        var tsl = new TageScLPredictor();
        var bnp = new BranchNetPredictor();
        const ulong branch = 0x3000;

        for (var i = 0; i < 200; i++) {
            bool taken = i % 7 < 4; // arbitrary periodic pattern, learnable by TAGE alone
            BranchPrediction pt = tsl.Predict(branch);
            BranchPrediction pb = bnp.Predict(branch);
            Assert.Equal(pt.PredictedTaken, pb.PredictedTaken);
            tsl.Update(branch, taken, taken ? branch - 0x100 : branch + 4);
            bnp.Update(branch, taken, taken ? branch - 0x100 : branch + 4);
        }
    }

    [Fact]
    public void FromProfile_TrainsModelForH2PBranchAndBeatsTageScL() {
        // A branch driven by a 24-bit maximal-length LFSR over its own global history
        // (feedback = XOR of bits 16, 21, 22, 23 — the primitive polynomial x^24+x^23+x^22+x^17+1).
        // Its period (2^24-1) vastly exceeds any test window, so within that window it looks
        // statistically unpredictable to TAGE-SC-L's fold-hashed tables (empirically ~50%
        // mispredict rate) — a legitimate H2P branch — while still being a deterministic
        // function of history that BranchNet's per-position embeddings can fit. Both the
        // profiling pass and the later online evaluation replay the same deterministic
        // history-driven oracle sequence (starting from the same nonzero seed, since an
        // all-zero LFSR state is a degenerate fixed point), so the comparison measures whether
        // offline training actually fit the pattern, not out-of-distribution generalization.
        const ulong branch = 0x9000;
        var decoder = new FakeBranchDecoder(branch);

        var profiler = new BranchNetPredictor.BranchProfiler(decoder);
        var state = new FakeArchState();
        ulong oracleGhr = 0x123456;
        const int profileSteps = 3000;

        for (var i = 0; i < profileSteps; i++) {
            bool taken = OracleTaken(oracleGhr);
            state.Pc = taken ? branch - 0x100 : branch + 4;
            profiler.OnCommit(branch, 0, state);
            oracleGhr = ((oracleGhr << 1) | (taken ? 1UL : 0UL)) & 0xFFFFFF;
        }

        BranchNetPredictor bnp = BranchNetPredictor.FromProfile(profiler);
        Assert.Equal(1, bnp.TrainedModelCount);

        var tsl = new TageScLPredictor();
        oracleGhr = 0x123456;
        int tslMisses = 0, bnpMisses = 0;
        const int evalSteps = 3000;

        for (var i = 0; i < evalSteps; i++) {
            bool taken = OracleTaken(oracleGhr);

            BranchPrediction pt = tsl.Predict(branch);
            BranchPrediction pb = bnp.Predict(branch);
            if (pt.PredictedTaken != taken) tslMisses++;
            if (pb.PredictedTaken != taken) bnpMisses++;

            ulong target = taken ? branch - 0x100 : branch + 4;
            tsl.Update(branch, taken, target);
            bnp.Update(branch, taken, target);

            oracleGhr = ((oracleGhr << 1) | (taken ? 1UL : 0UL)) & 0xFFFFFF;
        }

        Assert.True(
            bnpMisses < tslMisses,
            $"Expected BranchNet's trained model to beat TAGE-SC-L on a history-parity pattern " +
            $"(BranchNet misses={bnpMisses}, TAGE-SC-L misses={tslMisses})"
        );
    }

    // 24-bit maximal-length LFSR feedback: XOR of history bits 16, 21, 22, 23.
    private static bool OracleTaken(ulong ghr) {
        bool b16 = ((ghr >> 16) & 1) != 0;
        bool b21 = ((ghr >> 21) & 1) != 0;
        bool b22 = ((ghr >> 22) & 1) != 0;
        bool b23 = ((ghr >> 23) & 1) != 0;
        return b16 ^ b21 ^ b22 ^ b23;
    }

    private sealed class FakeBranchDecoder(ulong branchPc) : IDecoder {
        public ITooth Decode(ulong pc, IMemory memory) => throw new NotImplementedException();
        public ITooth Decode(ulong pc, uint raw) => throw new NotImplementedException();
        public int InstructionSize(ulong pc, IMemory memory) => throw new NotImplementedException();

        public FetchHint GetFetchHint(ulong pc, uint firstWord) =>
            new() { IsBranch = pc == branchPc, };
    }

    private sealed class FakeArchState : IArchState {
        public ulong Pc { get; set; }
        public PrivilegeLevel PrivilegeLevel { get; set; }

        public IRegisterFile IntegerRegisters {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }

        public ISystemRegisters SystemRegisters => throw new NotImplementedException();
        public IArchState Snapshot() => throw new NotImplementedException();
        public void Reset() { }
    }
}