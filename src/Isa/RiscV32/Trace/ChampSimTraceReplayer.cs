#region

using Mechanism;
using Orrery.Cache;

#endregion

namespace RiscV32.Trace;

/// <summary>
///     Statistics from replaying a ChampSim trace through a branch predictor and/or a cache.
///     Either evaluation is optional (pass <c>null</c> for the corresponding component to
///     <see cref="ChampSimTraceReplayer.Replay" /> to skip it).
/// </summary>
public readonly record struct ChampSimReplayResult(
    long Instructions,
    long Branches,
    long Mispredictions,
    long Loads,
    long Stores,
    long CacheHits,
    long CacheMisses
) {
    public double MispredictionRate => Branches == 0 ? 0.0 : (double)Mispredictions / Branches;

    /// <summary>Mispredictions per kilo-instruction, counted over all instructions (the standard CBP metric).</summary>
    public double Mpki => Instructions == 0 ? 0.0 : Mispredictions / (Instructions / 1000.0);

    public double CacheHitRate => CacheHits + CacheMisses == 0 ? 0.0 : (double)CacheHits / (CacheHits + CacheMisses);
}

/// <summary>
///     Replays a ChampSim trace against Horologium's <see cref="IBranchPredictor" /> and
///     <see cref="IReplacementPolicy" /> (via <see cref="SetAssociativeCache" />) surfaces, so
///     third-party CBP/CRC-style implementations can be cross-checked against real trace corpuses
///     rather than only Horologium-generated workloads.
/// </summary>
public static class ChampSimTraceReplayer {
    /// <summary>
    ///     Feeds <paramref name="records" /> through <paramref name="predictor" /> (branch direction
    ///     and target accuracy) and <paramref name="cache" /> (load/store hit rate), in a single pass.
    ///     A branch's actual target is the next record's <c>Ip</c> — ChampSim traces carry no
    ///     instruction length, but since the trace is the dynamic execution stream, the following
    ///     instruction's PC is always the true next-fetch address, taken or not. Consequently, the
    ///     last branch in the trace has no known outcome to score and is not counted.
    /// </summary>
    public static ChampSimReplayResult Replay(
        IEnumerable<ChampSimTraceRecord> records,
        IBranchPredictor? predictor = null,
        SetAssociativeCache? cache = null
    ) {
        long instructions = 0, branches = 0, mispredictions = 0, loads = 0, stores = 0;

        var havePendingBranch = false;
        ulong pendingPc = 0;
        var pendingActualTaken = false;
        BranchPrediction pendingPrediction = default;

        foreach (ChampSimTraceRecord rec in records) {
            if (havePendingBranch) {
                bool correct = pendingPrediction.PredictedTaken == pendingActualTaken &&
                               (!pendingActualTaken || pendingPrediction.PredictedTarget == rec.Ip);
                if (!correct) mispredictions++;
                predictor!.Update(pendingPc, pendingActualTaken, rec.Ip);
                havePendingBranch = false;
            }

            instructions++;

            if (predictor != null && rec.IsBranch) {
                branches++;
                pendingPrediction = predictor.Predict(rec.Ip);
                pendingPc = rec.Ip;
                pendingActualTaken = rec.BranchTaken;
                havePendingBranch = true;
            }

            if (cache != null) {
                cache.SetRequestPc(rec.Ip);
                foreach (ulong addr in rec.SourceMemory) {
                    if (addr == 0) continue;
                    loads++;
                    cache.Read(addr, 1);
                }

                foreach (ulong addr in rec.DestinationMemory) {
                    if (addr == 0) continue;
                    stores++;
                    cache.Write(addr, 0, 1);
                }
            }
        }

        return new ChampSimReplayResult(
            instructions, branches, mispredictions, loads, stores,
            cache?.Hits ?? 0, cache?.Misses ?? 0
        );
    }
}