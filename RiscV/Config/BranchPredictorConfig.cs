using System.Text.Json.Serialization;
using Mechanism;
using Mechanism.BranchPredictModels;

namespace RiscV.Config;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AlwaysNotTakenConfig), "always_not_taken")]
[JsonDerivedType(typeof(AlwaysTakenConfig), "always_taken")]
[JsonDerivedType(typeof(OneBitConfig), "one_bit")]
[JsonDerivedType(typeof(TwoBitConfig), "two_bit")]
[JsonDerivedType(typeof(CorrelatedConfig), "correlated")]
[JsonDerivedType(typeof(GselectConfig), "gselect")]
[JsonDerivedType(typeof(GshareConfig), "gshare")]
public abstract record BranchPredictorConfig {
    public abstract IBranchPredictor Build();

    public static BranchPredictorConfig AlwaysNotTaken() => new AlwaysNotTakenConfig();
    public static BranchPredictorConfig AlwaysTaken() => new AlwaysTakenConfig();
    public static BranchPredictorConfig OneBit(int tableSize = 1024) => new OneBitConfig(tableSize);
    public static BranchPredictorConfig TwoBit(int tableSize = 1024) => new TwoBitConfig(tableSize);

    public static BranchPredictorConfig Correlated(int m = 2, int n = 2, int bhtSize = 1024) =>
        new CorrelatedConfig(m, n, bhtSize);

    public static BranchPredictorConfig Gselect(int historyBits = 4, int pcBits = 4) =>
        new GselectConfig(historyBits, pcBits);

    public static BranchPredictorConfig Gshare(int historyBits = 8) => new GshareConfig(historyBits);
}

public sealed record AlwaysNotTakenConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new AlwaysNotTakenPredictor();
}

public sealed record AlwaysTakenConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new AlwaysTakenPredictor();
}

public sealed record OneBitConfig(int TableSize = 1024) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new OneBitPredictor(TableSize);
}

public sealed record TwoBitConfig(int TableSize = 1024) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new TwoBitPredictor(TableSize);
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