using System.Text.Json.Serialization;
using Mechanism;
using Mechanism.BranchPredictModels;
using Pipeline;
using RiscV32.Memory;

namespace RiscV32.Config;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AlwaysNotTakenConfig), "always_not_taken")]
[JsonDerivedType(typeof(AlwaysTakenConfig), "always_taken")]
[JsonDerivedType(typeof(AlwaysBackwardNotForwardsConfig), "always_backward_not_forwards")]
[JsonDerivedType(typeof(NBitConfig), "n_bit")]
[JsonDerivedType(typeof(CorrelatedConfig), "correlated")]
[JsonDerivedType(typeof(GselectConfig), "gselect")]
[JsonDerivedType(typeof(GshareConfig), "gshare")]
[JsonDerivedType(typeof(LTageConfig), "l_tage")]
[JsonDerivedType(typeof(PerceptronConfig), "perceptron")]
[JsonDerivedType(typeof(TournamentConfig), "tournament")]
[JsonDerivedType(typeof(TageScLConfig), "tage_sc_l")]
[JsonDerivedType(typeof(HashedPerceptronConfig), "hashed_perceptron")]
[JsonDerivedType(typeof(IttageConfig), "ittage")]
[JsonDerivedType(typeof(BatageConfig), "batage")]
[JsonDerivedType(typeof(OracleConfig), "oracle")]
[JsonDerivedType(typeof(TrueOracleConfig), "true_oracle")]
[JsonDerivedType(typeof(ImliConfig), "imli")]
[JsonDerivedType(typeof(LlbpConfig), "llbp")]
[JsonDerivedType(typeof(LlbpXConfig), "llbp_x")]
[JsonDerivedType(typeof(VlaTageConfig), "vla_tage")]
public abstract record BranchPredictorConfig {
    public abstract IBranchPredictor Build();

    /// <summary>
    /// Builds a predictor with access to a functional pre-pass. Configs that need a workload
    /// trace (e.g. <see cref="TrueOracleConfig"/>) override this; all others delegate to Build().
    /// </summary>
    public virtual IBranchPredictor Build(IMechanism mechanism, IWorkload workload) => Build();

    public static BranchPredictorConfig AlwaysNotTaken() => new AlwaysNotTakenConfig();
    public static BranchPredictorConfig AlwaysTaken() => new AlwaysTakenConfig();
    public static BranchPredictorConfig AlwaysBackwardNotForwards() => new AlwaysBackwardNotForwardsConfig();
    public static BranchPredictorConfig NBit(int bits = 2, int tableSize = 1024) => new NBitConfig(bits, tableSize);

    public static BranchPredictorConfig Correlated(int m = 2, int n = 2, int bhtSize = 1024) =>
        new CorrelatedConfig(m, n, bhtSize);

    public static BranchPredictorConfig Gselect(int historyBits = 4, int pcBits = 4) =>
        new GselectConfig(historyBits, pcBits);

    public static BranchPredictorConfig Gshare(int historyBits = 8) => new GshareConfig(historyBits);

    public static BranchPredictorConfig LTage() => new LTageConfig();

    public static BranchPredictorConfig Perceptron(int historyLength = 24, int tableSize = 256) =>
        new PerceptronConfig(historyLength, tableSize);

    public static BranchPredictorConfig Tournament(
        int localHistoryBits = 10,
        int localTableSize = 1024,
        int globalHistoryBits = 12
    ) => new TournamentConfig(localHistoryBits, localTableSize, globalHistoryBits);

    public static BranchPredictorConfig TageScL() => new TageScLConfig();

    public static BranchPredictorConfig HashedPerceptron(int tableSize = 512) =>
        new HashedPerceptronConfig(tableSize);

    public static BranchPredictorConfig Ittage() => new IttageConfig();
    public static BranchPredictorConfig Batage() => new BatageConfig();
    public static BranchPredictorConfig Oracle() => new OracleConfig();
    public static BranchPredictorConfig TrueOracle() => new TrueOracleConfig();

    public static BranchPredictorConfig Imli(int phtSize = 65536, int btbSize = 1024) =>
        new ImliConfig(phtSize, btbSize);

    public static BranchPredictorConfig Llbp() => new LlbpConfig();
    public static BranchPredictorConfig LlbpX() => new LlbpXConfig();
    public static BranchPredictorConfig VlaTage() => new VlaTageConfig();
}

public sealed record AlwaysNotTakenConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new AlwaysNotTakenPredictor();
}

public sealed record AlwaysTakenConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new AlwaysTakenPredictor();
}

public sealed record AlwaysBackwardNotForwardsConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new AlwaysBackwardNotForwards();
}

public sealed record NBitConfig(int Bits = 2, int TableSize = 1024) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new NBitPredictor(Bits, TableSize);
}

public sealed record CorrelatedConfig(int M = 2, int N = 2, int BhtSize = 1024) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new CorrelatedPredictor(M, N, BhtSize);
}

public sealed record GselectConfig(int HistoryBits = 4, int PcBits = 4) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new GselectPredictor(HistoryBits, PcBits);
}

public sealed record GshareConfig(int HistoryBits = 8) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new GsharePredictor(HistoryBits);
}

public sealed record LTageConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new LTagePredictor();
}

public sealed record PerceptronConfig(int HistoryLength = 24, int TableSize = 256) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new PerceptronPredictor(HistoryLength, TableSize);
}

public sealed record TournamentConfig(
    int LocalHistoryBits = 10,
    int LocalTableSize = 1024,
    int GlobalHistoryBits = 12
) : BranchPredictorConfig {
    public override IBranchPredictor Build() =>
        new TournamentPredictor(LocalHistoryBits, LocalTableSize, GlobalHistoryBits);
}

public sealed record TageScLConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new TageScLPredictor();
}

public sealed record HashedPerceptronConfig(int TableSize = 512) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new HashedPerceptronPredictor(TableSize);
}

public sealed record IttageConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new IttagePredictor();
}

public sealed record BatageConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new BatagePredictor();
}

public sealed record OracleConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new OraclePredictor();
}

public sealed record ImliConfig(int PhtSize = 65536, int BtbSize = 1024) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new ImliPredictor(PhtSize, BtbSize);
}

public sealed record LlbpConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new LlbpPredictor();
}

public sealed record LlbpXConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new LlbpXPredictor();
}

public sealed record VlaTageConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new VlaTagePredictor();
}

public sealed record TrueOracleConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() =>
        throw new InvalidOperationException(
            "TrueOracleConfig requires a functional pre-pass. Call Build(mechanism, workload) instead."
        );

    public override IBranchPredictor Build(IMechanism mechanism, IWorkload workload) {
        var preMemory = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(preMemory);
        var recorder = new BranchTraceRecorder(mechanism.Decoder);
        new SingleCycleTrain(
            mechanism, workload.WrapMemory(preMemory), workload.EntryPoint,
            commitObserver: recorder
        ).Run(long.MaxValue);
        return new TrueOraclePredictor(recorder.Trace);
    }
}