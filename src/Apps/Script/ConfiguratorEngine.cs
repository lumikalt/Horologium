#region

using Mechanism;
using Microsoft.CodeAnalysis.Scripting;
using Orrery.Cache;
using Orrery.Observation;
using Pipeline.Spec;
using RiscV32.Memory;

#endregion

namespace Script;

/// <summary>Result of evaluating a script and building the machine it describes.</summary>
public sealed record ConfiguratorBuildResult(MachineHandle? Handle, string? Error) {
    public bool Success => Handle is not null;
}

/// <summary>Cache/TLB counters and dial snapshots for the configurator's live stat display.</summary>
public sealed record ConfiguratorStats(
    IReadOnlyList<DialBoardSnapshot> Dials,
    long? L1Hits,
    long? L1Misses,
    long? L2Hits,
    long? L2Misses,
    long? L3Hits,
    long? L3Misses,
    long? TlbHits,
    long? TlbMisses
);

/// <summary>
///     UI-framework-free bridge between a `.csx` architecture script and a running
///     <see cref="MachineHandle" />, for Face's Configurator tab. Evaluation errors (Roslyn
///     compile errors, a script that doesn't return a <see cref="MachineSpec" />, or a build-time
///     exception) are captured in <see cref="ConfiguratorBuildResult.Error" /> rather than thrown,
///     so a caller mid-edit can keep the previous good <see cref="MachineHandle" /> running.
/// </summary>
public static class ConfiguratorEngine {
    /// <summary>
    ///     Evaluates <paramref name="scriptSource" /> and builds the machine it describes against
    ///     <paramref name="workload" />.
    /// </summary>
    public static async Task<ConfiguratorBuildResult> BuildAsync(
        string scriptSource,
        IWorkload workload,
        CancellationToken ct = default
    ) {
        MachineSpec spec;
        try { spec = await ScriptHost.EvaluateCsxAsync(scriptSource, ct); }
        catch (Exception ex) when (ex is ScriptException or CompilationErrorException) {
            return new ConfiguratorBuildResult(null, ex.Message);
        }

        try {
            var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
            workload.Load(mem);
            IMemory backing = workload.WrapMemory(mem);
            MachineHandle handle = spec.Build(backing, workload.EntryPoint, workload.MmioRegion);
            return new ConfiguratorBuildResult(handle, null);
        }
        catch (Exception ex) { return new ConfiguratorBuildResult(null, ex.Message); }
    }

    /// <summary>Snapshots dials and cache/TLB counters for the live stat display. Safe to call repeatedly mid-run.</summary>
    public static ConfiguratorStats SnapshotStats(MachineHandle handle) {
        IReadOnlyList<DialBoardSnapshot> dials;
        try { dials = handle.Train.SnapshotDials(); }
        catch (NotSupportedException) { dials = []; }

        MemoryLayers? layers = handle.Layers;
        return new ConfiguratorStats(
            dials,
            layers?.Cache?.Hits, layers?.Cache?.Misses,
            layers?.L2Cache?.Hits, layers?.L2Cache?.Misses,
            layers?.L3Cache?.Hits, layers?.L3Cache?.Misses,
            layers?.Tlb?.Hits, layers?.Tlb?.Misses
        );
    }
}