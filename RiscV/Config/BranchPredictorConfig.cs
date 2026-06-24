using System.Text.Json.Serialization;
using Mechanism;
using Mechanism.BranchPredictModels;

namespace RiscV.Config;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AlwaysNotTakenConfig), "always_not_taken")]
[JsonDerivedType(typeof(AlwaysTakenConfig), "always_taken")]
[JsonDerivedType(typeof(AlwaysBackwardNotForwardsConfig), "always_backward_not_forwards")]
[JsonDerivedType(typeof(NBitConfig), "n_bit")]
[JsonDerivedType(typeof(CorrelatedConfig), "correlated")]
[JsonDerivedType(typeof(GselectConfig), "gselect")]
[JsonDerivedType(typeof(GshareConfig), "gshare")]
[JsonDerivedType(typeof(LTageConfig), "l_tage")]
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