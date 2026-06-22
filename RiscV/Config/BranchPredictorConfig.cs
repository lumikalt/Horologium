using System.Text.Json.Serialization;
using Mechanism;
using Mechanism.BranchPredictModels;

namespace RiscV.Config;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AlwaysNotTakenConfig), "always_not_taken")]
[JsonDerivedType(typeof(AlwaysTakenConfig), "always_taken")]
[JsonDerivedType(typeof(OneBitConfig), "one_bit")]
[JsonDerivedType(typeof(TwoBitConfig), "two_bit")]
public abstract record BranchPredictorConfig {
    public abstract IBranchPredictor Build();

    public static BranchPredictorConfig AlwaysNotTaken() => new AlwaysNotTakenConfig();
    public static BranchPredictorConfig AlwaysTaken() => new AlwaysTakenConfig();
    public static BranchPredictorConfig OneBit(int tableSize = 1024) => new OneBitConfig(tableSize);
    public static BranchPredictorConfig TwoBit(int tableSize = 1024) => new TwoBitConfig(tableSize);
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
