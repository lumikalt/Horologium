using System.Text.Json.Serialization;
using Mechanism;
using Mechanism.BranchPredictModels;

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
public abstract record BranchPredictorConfig {
    public abstract IBranchPredictor Build();

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