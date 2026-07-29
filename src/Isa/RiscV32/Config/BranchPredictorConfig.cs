#region

using System.Text.Json.Serialization;
using Mechanism;
using Mechanism.BranchPred;
using Mechanism.RtlFu;
using Pipeline;
using RiscV32.Memory;

#endregion

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
[JsonDerivedType(typeof(ImliConfig), "imli")]
[JsonDerivedType(typeof(LlbpConfig), "llbp")]
[JsonDerivedType(typeof(LlbpXConfig), "llbp_x")]
[JsonDerivedType(typeof(VlaTageConfig), "vla_tage")]
[JsonDerivedType(typeof(RunltsConfig), "runlts")]
[JsonDerivedType(typeof(LvcpConfig), "lvcp")]
[JsonDerivedType(typeof(BranchNetConfig), "branchnet")]
[JsonDerivedType(typeof(TeaConfig), "tea")]
[JsonDerivedType(typeof(CbpPluginConfig), "cbp_plugin")]
[JsonDerivedType(typeof(RtlBpPluginConfig), "rtl_bp_plugin")]
[JsonDerivedType(typeof(CbpNgPluginConfig), "cbp_ng_plugin")]
[JsonDerivedType(typeof(CbpNgOoOePluginConfig), "cbp_ng_ooo_plugin")]
[JsonDerivedType(typeof(BullseyeConfig), "bullseye")]
[JsonDerivedType(typeof(HypreConfig), "hypre")]
[JsonDerivedType(typeof(MultiperspectivePerceptronConfig), "multiperspective_perceptron")]
public abstract record BranchPredictorConfig {
    public abstract IBranchPredictor Build();

    /// <summary>
    ///     Builds a predictor with access to a functional pre-pass. Configs that need a workload
    ///     trace (e.g. <see cref="OracleConfig" />) override this; all others delegate to Build().
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

    public static BranchPredictorConfig Imli(int phtSize = 65536, int btbSize = 1024) =>
        new ImliConfig(phtSize, btbSize);

    public static BranchPredictorConfig Llbp() => new LlbpConfig();
    public static BranchPredictorConfig LlbpX() => new LlbpXConfig();
    public static BranchPredictorConfig VlaTage() => new VlaTageConfig();
    public static BranchPredictorConfig Runlts() => new RunltsConfig();
    public static BranchPredictorConfig Lvcp() => new LvcpConfig();
    public static BranchPredictorConfig BranchNet() => new BranchNetConfig();
    public static BranchPredictorConfig Tea() => new TeaConfig();

    public static BranchPredictorConfig CbpPlugin(string libraryPath) => new CbpPluginConfig(libraryPath);

    public static BranchPredictorConfig CbpNgPlugin(string libraryPath) => new CbpNgPluginConfig(libraryPath);

    public static BranchPredictorConfig CbpNgOoOePlugin(string libraryPath) =>
        new CbpNgOoOePluginConfig(libraryPath);

    public static BranchPredictorConfig Bullseye() => new BullseyeConfig();
    public static BranchPredictorConfig Hypre() => new HypreConfig();
    public static BranchPredictorConfig MultiperspectivePerceptron() => new MultiperspectivePerceptronConfig();
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
    public override IBranchPredictor Build() => new NBitBp(Bits, TableSize);
}

public sealed record CorrelatedConfig(int M = 2, int N = 2, int BhtSize = 1024) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new CorrelatedBp(M, N, BhtSize);
}

public sealed record GselectConfig(int HistoryBits = 4, int PcBits = 4) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new GselectPredictor(HistoryBits, PcBits);
}

public sealed record GshareConfig(int HistoryBits = 8) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new GshareBp(HistoryBits);
}

public sealed record LTageConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new LTageBp();
}

public sealed record PerceptronConfig(int HistoryLength = 24, int TableSize = 256) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new PerceptronBp(HistoryLength, TableSize);
}

public sealed record TournamentConfig(
    int LocalHistoryBits = 10,
    int LocalTableSize = 1024,
    int GlobalHistoryBits = 12
) : BranchPredictorConfig {
    public override IBranchPredictor Build() =>
        new TournamentBp(LocalHistoryBits, LocalTableSize, GlobalHistoryBits);
}

public sealed record TageScLConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new TageScLBp();
}

public sealed record HashedPerceptronConfig(int TableSize = 512) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new HashedPerceptronBp(TableSize);
}

public sealed record IttageConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new IttagePredictor();
}

public sealed record BatageConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new BatageBp();
}

public sealed record ImliConfig(int PhtSize = 65536, int BtbSize = 1024) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new ImliPredictor(PhtSize, BtbSize);
}

public sealed record LlbpConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new LlbpBp();
}

public sealed record LlbpXConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new LlbpXBp();
}

public sealed record VlaTageConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new VlaTageBp();
}

public sealed record OracleConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() =>
        throw new InvalidOperationException(
            "OracleConfig requires a functional pre-pass. Call Build(mechanism, workload) instead."
        );

    public override IBranchPredictor Build(IMechanism mechanism, IWorkload workload) {
        var preMemory = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(preMemory);
        var recorder = new BranchTraceRecorder(mechanism.Decoder);
        new SingleCycleTrain(
            mechanism, workload.WrapMemory(preMemory), workload.EntryPoint,
            commitObserver: recorder
        ).Run(long.MaxValue);
        return new OracleBp(recorder.Trace);
    }
}

public sealed record RunltsConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new RunltsBp();
}

public sealed record LvcpConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new LvcpBp();
}

public sealed record BranchNetConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() =>
        throw new InvalidOperationException(
            "BranchNetConfig requires a functional pre-pass. Call Build(mechanism, workload) instead."
        );

    public override IBranchPredictor Build(IMechanism mechanism, IWorkload workload) {
        var preMemory = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(preMemory);
        var profiler = new BranchNetBp.BranchProfiler(mechanism.Decoder);
        new SingleCycleTrain(
            mechanism, workload.WrapMemory(preMemory), workload.EntryPoint,
            commitObserver: profiler
        ).Run(long.MaxValue);
        return BranchNetBp.FromProfile(profiler);
    }
}

public sealed record TeaConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() =>
        throw new InvalidOperationException(
            "TeaConfig requires a functional pre-pass. Call Build(mechanism, workload) instead."
        );

    public override IBranchPredictor Build(IMechanism mechanism, IWorkload workload) {
        var preMemory = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(preMemory);
        var profiler = new TeaBp.TeaProfiler(mechanism.Decoder);
        new SingleCycleTrain(
            mechanism, workload.WrapMemory(preMemory), workload.EntryPoint,
            commitObserver: profiler
        ).Run(long.MaxValue);
        return TeaBp.FromProfile(profiler);
    }
}

/// <summary>
///     Loads a CBP-3/CBP-5-style third-party predictor from a native shared library built via
///     <c>native/CbpShim/build.sh</c>. Desktop-only — see <see cref="CbpFfiBp" />.
/// </summary>
public sealed record CbpPluginConfig(string LibraryPath) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new CbpFfiBp(LibraryPath);
}

/// <summary>
///     Loads a Verilator-compiled RTL branch predictor from a native shared library built via
///     <c>native/RtlFu/build.sh &lt;sv&gt; &lt;top&gt; &lt;out.so&gt; rtl_bp_shim.cpp</c> (plain
///     predictor) or <c>… rtl_hbp_shim.cpp</c> (speculative-history predictor, e.g., the L-TAGE);
///     the shim ABI is auto-detected. Desktop-only — see <see cref="RtlFfiBp" /> and
///     <see cref="RtlFfiHistoryBp" />.
/// </summary>
public sealed record RtlBpPluginConfig(string LibraryPath) : BranchPredictorConfig {
    public override IBranchPredictor Build() => RtlBranchPredictorLoader.Load(LibraryPath);
}

/// <summary>
///     Loads a CBP2025/CBP-NG (AmpereComputing/cbp-ng) predictor from a native shared library
///     built via <c>native/CbpNgShim/build.sh</c>, driven live at fetch time. Desktop-only — see
///     <see cref="CbpNgFfiBp" />. Only safe for <c>SingleCycleTrain</c>, which never
///     overlaps an unresolved branch's <c>Predict</c>/<c>Update</c> with another branch's; use
///     <see cref="CbpNgOoOePluginConfig" /> for any other pipeline.
/// </summary>
public sealed record CbpNgPluginConfig(string LibraryPath) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new CbpNgFfiBp(LibraryPath);
}

/// <summary>
///     Loads a CBP2025/CBP-NG predictor the same way as <see cref="CbpNgPluginConfig" />, but
///     wrapped in <see cref="CbpNgCommitDrivenBp" /> so it is safe with pipelines that keep
///     multiple unresolved predictions in flight (<c>FiveStageTrain</c>, <c>OooTrain</c>) — see
///     docs/pipeline-trains.md. Not suitable for <c>CprTrain</c> (trains
///     predictors out of program order at execution).
/// </summary>
public sealed record CbpNgOoOePluginConfig(string LibraryPath) : BranchPredictorConfig {
    public override IBranchPredictor Build() => new CbpNgCommitDrivenBp(LibraryPath);
}

public sealed record BullseyeConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new BullseyeBp();
}

public sealed record HypreConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new HypreBp();
}

public sealed record MultiperspectivePerceptronConfig : BranchPredictorConfig {
    public override IBranchPredictor Build() => new MultiperspectivePerceptronBp();
}