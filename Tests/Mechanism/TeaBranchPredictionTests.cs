#region

using Mechanism;
using Mechanism.BranchPredictModels;

#endregion

namespace Tests.Mechanism;

public class TeaBranchPredictionTests {
    private const ulong ProducerPc = 0x2000;
    private const ulong BranchPc = 0x2004;
    private const int ProducerDestReg = 1;

    [Fact]
    public void ColdMiss_PredictsFallThrough() {
        var p = new TeaPredictor();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
        Assert.Equal(0, p.TrackedBranchCount);
    }

    [Fact]
    public void NoBlockCacheEntry_BehavesLikeTageScL() {
        // With an empty Block Cache (no FromProfile pre-pass), every branch falls through to
        // the internal TAGE-SC-L baseline unchanged.
        var tsl = new TageScLPredictor();
        var tea = new TeaPredictor();
        const ulong branch = 0x3000;

        for (var i = 0; i < 200; i++) {
            bool taken = i % 7 < 4; // arbitrary periodic pattern, learnable by TAGE alone
            BranchPrediction pt = tsl.Predict(branch);
            BranchPrediction pb = tea.Predict(branch);
            Assert.Equal(pt.PredictedTaken, pb.PredictedTaken);
            tsl.Update(branch, taken, taken ? branch - 0x100 : branch + 4);
            tea.Update(branch, taken, taken ? branch - 0x100 : branch + 4);
        }

        Assert.Equal(0, tea.TeaOverrides);
    }

    [Fact]
    public void FromProfile_DiscoversChainAndBeatsTageScLForRegisterCorrelatedPattern() {
        // A two-instruction dataflow chain: an ALU producer at ProducerPc writes register 1,
        // and a conditional branch at BranchPc reads it as its sole compare operand. The
        // branch's direction is a pseudorandom function of loop iteration (defeats
        // history-based TAGE-SC-L) but is perfectly signaled by the producer's register
        // value — exactly the correlation the Backward Dataflow Walk is meant to discover
        // (rather than assume, the way RunltsPredictor's "most recent write" heuristic does).
        var decoder = new FakeDecoder(
            new Dictionary<ulong, ITooth> {
                [TeaBranchPredictionTests.ProducerPc] = new FakeTooth {
                    Pc = TeaBranchPredictionTests.ProducerPc,
                    DestinationRegister = TeaBranchPredictionTests.ProducerDestReg,
                },
                [TeaBranchPredictionTests.BranchPc] = new FakeTooth {
                    Pc = TeaBranchPredictionTests.BranchPc,
                    SourceRegisters = [TeaBranchPredictionTests.ProducerDestReg,], Class = ToothClass.ConditionalBranch,
                },
            }
        );

        var profiler = new TeaPredictor.TeaProfiler(decoder);
        var state = new FakeArchState();
        var rng = new Random(1234);
        const int profileSteps = 600;

        for (var i = 0; i < profileSteps; i++) {
            bool taken = rng.Next(2) == 0;

            state.Pc = TeaBranchPredictionTests.ProducerPc + 4; // fall-through, non-branch
            profiler.OnCommit(TeaBranchPredictionTests.ProducerPc, 0, state);

            state.Pc = taken ? TeaBranchPredictionTests.BranchPc - 0x100 : TeaBranchPredictionTests.BranchPc + 4;
            profiler.OnCommit(TeaBranchPredictionTests.BranchPc, 0, state);
        }

        TeaPredictor tea = TeaPredictor.FromProfile(profiler);
        Assert.Equal(1, tea.TrackedBranchCount);

        var tsl = new TageScLPredictor();
        rng = new Random(1234); // replay the same pattern for the online comparison
        int tslMisses = 0, teaMisses = 0;
        const ulong takenValue = 0xAAAAAAAAAAAAAAAAUL;
        const ulong notTakenValue = 0x5555555555555555UL;

        for (var i = 0; i < profileSteps; i++) {
            bool taken = rng.Next(2) == 0;

            // Producer executes before the branch is fetched.
            tea.NotifyRegisterResult(
                TeaBranchPredictionTests.ProducerPc, TeaBranchPredictionTests.ProducerDestReg,
                taken ? takenValue : notTakenValue, false
            );

            BranchPrediction pt = tsl.Predict(TeaBranchPredictionTests.BranchPc);
            BranchPrediction pb = tea.Predict(TeaBranchPredictionTests.BranchPc);
            if (pt.PredictedTaken != taken) tslMisses++;
            if (pb.PredictedTaken != taken) teaMisses++;

            ulong target = taken ? TeaBranchPredictionTests.BranchPc - 0x100 : TeaBranchPredictionTests.BranchPc + 4;
            tsl.Update(TeaBranchPredictionTests.BranchPc, taken, target);
            tea.Update(TeaBranchPredictionTests.BranchPc, taken, target);
        }

        Assert.True(
            teaMisses < tslMisses / 2,
            $"Expected TEA's discovered dependence chain to substantially beat history-only TAGE-SC-L " +
            $"on a register-correlated pseudorandom pattern (TEA misses={teaMisses}, TAGE-SC-L misses={tslMisses})"
        );
        Assert.True(tea.TeaOverrides > 0, "Expected TEA to have overridden the baseline at least once");
    }

    [Fact]
    public void UnrelatedRegisterWrites_DoNotPolluteChainOperands() {
        // NotifyRegisterResult must ignore PCs that are not part of any discovered chain —
        // otherwise an unrelated instruction happening to share a PC-derived index could
        // corrupt a tracked producer's recorded value.
        var decoder = new FakeDecoder(
            new Dictionary<ulong, ITooth> {
                [TeaBranchPredictionTests.ProducerPc] = new FakeTooth {
                    Pc = TeaBranchPredictionTests.ProducerPc,
                    DestinationRegister = TeaBranchPredictionTests.ProducerDestReg,
                },
                [TeaBranchPredictionTests.BranchPc] = new FakeTooth {
                    Pc = TeaBranchPredictionTests.BranchPc,
                    SourceRegisters = [TeaBranchPredictionTests.ProducerDestReg,], Class = ToothClass.ConditionalBranch,
                },
            }
        );

        var profiler = new TeaPredictor.TeaProfiler(decoder);
        var state = new FakeArchState();
        var rng = new Random(42);
        const int profileSteps = 600;

        for (var i = 0; i < profileSteps; i++) {
            bool taken = rng.Next(2) == 0;
            state.Pc = TeaBranchPredictionTests.ProducerPc + 4;
            profiler.OnCommit(TeaBranchPredictionTests.ProducerPc, 0, state);
            state.Pc = taken ? TeaBranchPredictionTests.BranchPc - 0x100 : TeaBranchPredictionTests.BranchPc + 4;
            profiler.OnCommit(TeaBranchPredictionTests.BranchPc, 0, state);
        }

        TeaPredictor tea = TeaPredictor.FromProfile(profiler);
        Assert.Equal(1, tea.TrackedBranchCount);

        // Flood with register results from PCs never seen during profiling — must be ignored.
        for (var i = 0; i < 100; i++) tea.NotifyRegisterResult(0x9000UL + (ulong)i, 2, (ulong)i, false);

        BranchPrediction pred = tea.Predict(TeaBranchPredictionTests.BranchPc);
        Assert.Equal(0, tea.TeaOverrides); // chain operand was never actually reported fresh
        Assert.False(pred.PredictedTaken); // falls through to the cold TAGE-SC-L baseline
    }

    private sealed class FakeTooth : ITooth {
        public ulong Pc { get; init; }
        public uint RawEncoding => 0;
        public int SizeBytes => 4;
        public int DestinationRegister { get; init; } = -1;
        public IReadOnlyList<int> SourceRegisters { get; init; } = [];
        public ToothClass Class { get; init; }
        public object? Payload => null;
    }

    private sealed class FakeDecoder(Dictionary<ulong, ITooth> byPc) : IDecoder {
        public ITooth Decode(ulong pc, IMemory memory) => throw new NotImplementedException();
        public ITooth Decode(ulong pc, uint raw) => byPc[pc];
        public int InstructionSize(ulong pc, IMemory memory) => throw new NotImplementedException();
        public FetchHint GetFetchHint(ulong pc, uint firstWord) => throw new NotImplementedException();
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